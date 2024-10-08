﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using HtmlAgilityPack;

using SemanticSlicer.Models;

namespace SemanticSlicer
{
	/// <summary>
	/// A utility class for chunking and subdividing text content based on specified separators.
	/// </summary>
	public class Slicer : ISlicer
	{
		static readonly Regex LINE_ENDING_REGEX = new Regex(@"\r\n?|\n", RegexOptions.Compiled);
		static readonly string LINE_ENDING_REPLACEMENT = "\n";

		private SlicerOptions _options;
		private readonly Tiktoken.Encoder _encoding;

		/// <summary>
		/// Initializes a new instance of the <see cref="Slicer"/> class with optional SemanticSlicer options.
		/// </summary>
		/// <param name="options">Optional SemanticSlicer options.</param>
		public Slicer(SlicerOptions? options = null)
		{
			_options = options ?? new SlicerOptions();
			_encoding = Tiktoken.ModelToEncoder.TryFor(_options.Encoding) ?? throw new ArgumentNullException(nameof(Encoding));
		}


		/// <summary>
		/// Gets a list of document chunks for the given content.
		/// </summary>
		/// <param name="content">A string representing the content of the document to be chunked.</param>
		/// <param name="metadata">A dictionary representing the metadata of the document. It is a nullable parameter and its default value is null.</param>
		/// <param name="chunkHeader">A string representing the header of every chunk. It has a default value of an empty string. It will always have at least one newline character separating it from the chunk content.</param>
		/// <returns>Returns a list of DocumentChunks after performing a series of actions including normalization, token counting, splitting, indexing, and removing HTML tags, etc.</returns>
		public List<DocumentChunk> GetDocumentChunks(string content, Dictionary<string, object?>? metadata = null, string chunkHeader = "")
		{
			var massagedChunkHeader = chunkHeader;
			if (!string.IsNullOrWhiteSpace(chunkHeader))
			{
				if (!massagedChunkHeader.EndsWith(LINE_ENDING_REPLACEMENT))
				{
					massagedChunkHeader = $"{massagedChunkHeader}{LINE_ENDING_REPLACEMENT}";
				}
			}

			// make sure chunkHeader token count is less than maxChunkTokenCount
			var chunkHeaderTokenCount = _encoding.CountTokens(massagedChunkHeader);
			if (chunkHeaderTokenCount >= _options.MaxChunkTokenCount)
			{
				throw new ArgumentOutOfRangeException($"Chunk header token count ({chunkHeaderTokenCount}) is greater than max chunk token count ({_options.MaxChunkTokenCount})");
			}

			var massagedContent = NormalizeLineEndings(content).Trim();
			var effectiveTokenCount = _options.StripHtml
				? _encoding.CountTokens($"{massagedChunkHeader}{StripHtmlTags(massagedContent)}")
				: _encoding.CountTokens($"{massagedChunkHeader}{massagedContent}");

			var documentChunks = new List<DocumentChunk> {
				new DocumentChunk {
					Content = massagedContent,
					Metadata = metadata,
					TokenCount = effectiveTokenCount
				}
			};
			var chunks = SplitDocumentChunks(documentChunks, massagedChunkHeader);

			foreach (var chunk in chunks)
			{
				// Save the index with the chunk so they can be reassembled in the correct order
				chunk.Index = chunks.IndexOf(chunk);

				// Strip HTML tags from the content if requested
				if (_options.StripHtml)
					chunk.Content = StripHtmlTags(chunk.Content);
			}

			return chunks;
		}

		public string StripHtmlTags(string content)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(content);
			return doc.DocumentNode.InnerText;
		}

		/// <summary>
		/// Recursively subdivides a list of DocumentChunks into chunks that are less than or equal to maxTokens in length.
		/// </summary>
		/// <param name="documentChunks">The list of document chunks to be subdivided.</param>
		/// <param name="separators">The array of chunk separators.</param>
		/// <param name="maxTokens">The maximum number of tokens allowed in a chunk.</param>
		/// <returns>The list of subdivided document chunks.</returns>
		/// <exception cref="Exception">Thrown when unable to subdivide the string with given regular expressions.</exception>
		private List<DocumentChunk> SplitDocumentChunks(List<DocumentChunk> documentChunks, string chunkHeader)
		{
			var output = new List<DocumentChunk>();

			foreach (var documentChunk in documentChunks)
			{
				if (documentChunk.TokenCount <= _options.MaxChunkTokenCount)
				{
					documentChunk.Content = $"{chunkHeader}{documentChunk.Content}";
					output.Add(documentChunk);
					continue;
				}

				bool subdivided = false;
				foreach (var separator in _options.Separators)
				{
					var matches = separator.Regex.Matches(documentChunk.Content);
					if (matches.Count > 0)
					{
						Match? centermostMatch = GetCentermostMatch(documentChunk, matches);

						if (centermostMatch!.Index == 0)
						{
							continue;
						}

						var splitChunks = SplitChunkBySeparatorMatch(documentChunk, chunkHeader, separator, centermostMatch);

						if (IsSplitBelowThreshold(splitChunks))
						{
							continue;
						}

						// sanity check
						if (splitChunks.Item1.Content.Length < documentChunk.Content.Length && splitChunks.Item2.Content.Length < documentChunk.Content.Length)
						{
							output.AddRange(SplitDocumentChunks(new List<DocumentChunk> { splitChunks.Item1, splitChunks.Item2 }, chunkHeader));
						}

						subdivided = true;
						break;
					}
				}

				if (!subdivided)
				{
					throw new Exception("Unable to subdivide string with given regular expressions");
				}
			}

			return output;
		}

		/// <summary>
		/// Checks if the token percentage of either of the two provided chunks is below the defined threshold.
		/// </summary>
		/// <param name="splitChunks">A tuple containing two chunks of a document.</param>
		/// <returns>Returns true if either of the chunk's token percentage is below the threshold, otherwise false.</returns>
		private bool IsSplitBelowThreshold(Tuple<DocumentChunk, DocumentChunk> splitChunks)
		{
			// Deconstruct the tuple to get the first and second half of the split chunks
			(DocumentChunk firstHalfChunk, DocumentChunk secondHalfChunk) = splitChunks;

			// Calculate the token percentage of the first half of the chunk
			float firstHalfChunkPercentage = (float)firstHalfChunk.TokenCount / _options.MaxChunkTokenCount * 100;

			// Calculate the token percentage of the second half of the chunk
			float secondHalfChunkPercentage = (float)secondHalfChunk.TokenCount / _options.MaxChunkTokenCount * 100;

			// Return true if either of the chunk's token percentage is below the threshold
			return firstHalfChunkPercentage < _options.MinChunkPercentage || secondHalfChunkPercentage < _options.MinChunkPercentage;
		}

		private Tuple<DocumentChunk, DocumentChunk> SplitChunkBySeparatorMatch(DocumentChunk documentChunk, string chunkHeader, Separator separator, Match? match)
		{
			int matchIndex = match!.Index;
			var splitContent = DoTextSplit(documentChunk.Content, matchIndex, match.Value, separator.Behavior);

			var firstHalfContent = splitContent.Item1.Trim();
			var secondHalfContent = splitContent.Item2.Trim();

			var firstHalfEffectiveTokenCount = _options.StripHtml
				? _encoding.CountTokens($"{chunkHeader}{StripHtmlTags(firstHalfContent)}")
				: _encoding.CountTokens($"{chunkHeader}{firstHalfContent}");
			var secondHalfEffectiveTokenCount = _options.StripHtml
				? _encoding.CountTokens($"{chunkHeader}{StripHtmlTags(secondHalfContent)}")
				: _encoding.CountTokens($"{chunkHeader}{secondHalfContent}");

			var ret = new Tuple<DocumentChunk, DocumentChunk>(
				new DocumentChunk
				{
					Content = firstHalfContent,
					Metadata = documentChunk.Metadata,
					TokenCount = firstHalfEffectiveTokenCount
				},
				new DocumentChunk
				{
					Content = secondHalfContent,
					Metadata = documentChunk.Metadata,
					TokenCount = secondHalfEffectiveTokenCount
				}
			);

			return ret;
		}

		/// <summary>
		/// Finds the match in the given matches collection that is closest to the center of the document chunk.
		/// </summary>
		/// <param name="documentChunk">The document chunk.</param>
		/// <param name="matches">The matches collection.</param>
		/// <returns>The match that is closest to the center of the document chunk, or null if the matches collection is empty.</returns>
		private static Match? GetCentermostMatch(DocumentChunk documentChunk, MatchCollection matches)
		{
			// In the case where we're removing HTML tags from the chunks, it's too complex to try to find the
			// centermost match after stripping tags so we do it before, with the asuumption it will be close enough.
			int centerIndex = documentChunk.Content.Length / 2;
			Match? centermostMatch = null;
			int closestDistance = int.MaxValue;

			foreach (Match match in matches.Cast<Match>())
			{
				int distance = Math.Abs(centerIndex - match.Index);
				if (distance < closestDistance)
				{
					closestDistance = distance;
					centermostMatch = match;
				}
			}

			return centermostMatch;
		}

		/// <summary>
		/// Splits the content into two strings at the given matchIndex, using the given matchValue as a separator.
		/// The split point varies based on the separatorType.
		/// For example, if the separatorType is Prefix, the split point will be the beginning of the matchValue.
		/// If the separatorType is Suffix, the split point will be the end of the matchValue.
		/// If the separatorType is Default, the matching content will be removed when splitting.
		/// </summary>
		/// <param name="content"></param>
		/// <param name="matchIndex"></param>
		/// <param name="matchValue"></param>
		/// <param name="separatorType"></param>
		/// <returns></returns>
		/// <exception cref="Exception"></exception>
		private static Tuple<string, string> DoTextSplit(string content, int matchIndex, string matchValue, SeparatorBehavior separatorType)
		{
			int splitIndex1;
			int splitIndex2;

			if (separatorType == SeparatorBehavior.Prefix)
			{
				splitIndex1 = matchIndex;
				splitIndex2 = matchIndex;
			}
			else if (separatorType == SeparatorBehavior.Suffix)
			{
				splitIndex1 = matchIndex + matchValue.Length;
				splitIndex2 = matchIndex + matchValue.Length;
			}
			else if (separatorType == SeparatorBehavior.Remove)
			{
				splitIndex1 = matchIndex;
				splitIndex2 = matchIndex + matchValue.Length;
			}
			else
			{
				throw new Exception($"Unknown SeparatorType: {separatorType}");
			}

			return new Tuple<string, string>(content[..splitIndex1], content[splitIndex2..]);
		}

		/// <summary>
		/// Normalizes line endings in the input string.
		/// </summary>
		/// <param name="input">The input string.</param>
		/// <returns>The string with normalized line endings.</returns>
		private static string NormalizeLineEndings(string input)
		{
			return LINE_ENDING_REGEX.Replace(input, LINE_ENDING_REPLACEMENT);
		}
	}
}
