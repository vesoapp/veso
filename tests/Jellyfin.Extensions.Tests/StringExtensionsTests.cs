using System;
using Xunit;

namespace Jellyfin.Extensions.Tests
{
    public class StringExtensionsTests
    {
        [Theory]
        [InlineData("", "")] // Identity edge-case (no diactritics)
        [InlineData("Indiana Jones", "Indiana Jones")] // Identity (no diactritics)
        [InlineData("a\ud800b", "ab")] // Invalid UTF-16 char stripping
        [InlineData("Jön", "Jon")] // Issue #7484
        [InlineData("Jönssonligan", "Jonssonligan")] // Issue #7484
        [InlineData("Kieślowski", "Kieslowski")] // Issue #7450
        [InlineData("Cidadão Kane", "Cidadao Kane")] // Issue #7560
        [InlineData("운명처럼 널 사랑해", "운명처럼 널 사랑해")] // Issue #6393 (Korean language support)
        [InlineData("애타는 로맨스", "애타는 로맨스")] // Issue #6393
        public void RemoveDiacritics_ValidInput_Corrects(string input, string expectedResult)
        {
            string result = input.RemoveDiacritics();
            Assert.Equal(expectedResult, result);
        }

        [Theory]
        [InlineData("", false)] // Identity edge-case (no diactritics)
        [InlineData("Indiana Jones", false)] // Identity (no diactritics)
        [InlineData("a\ud800b", true)] // Invalid UTF-16 char stripping
        [InlineData("Jön", true)] // Issue #7484
        [InlineData("Jönssonligan", true)] // Issue #7484
        [InlineData("Kieślowski", true)] // Issue #7450
        [InlineData("Cidadão Kane", true)] // Issue #7560
        [InlineData("운명처럼 널 사랑해", false)] // Issue #6393 (Korean language support)
        [InlineData("애타는 로맨스", false)] // Issue #6393
        public void HasDiacritics_ValidInput_Corrects(string input, bool expectedResult)
        {
            bool result = input.HasDiacritics();
            Assert.Equal(expectedResult, result);
        }

        [Theory]
        [InlineData("", '_', 0)]
        [InlineData("___", '_', 3)]
        [InlineData("test\x00", '\x00', 1)]
        [InlineData("Imdb=tt0119567|Tmdb=330|TmdbCollection=328", '|', 2)]
        public void ReadOnlySpan_Count_Success(string str, char needle, int count)
        {
            Assert.Equal(count, str.AsSpan().Count(needle));
        }

        [Theory]
        [InlineData("", 'q', "")]
        [InlineData("Banana split", ' ', "Banana")]
        [InlineData("Banana split", 'q', "Banana split")]
        [InlineData("Banana split 2", ' ', "Banana")]
        public void LeftPart_ValidArgsCharNeedle_Correct(string str, char needle, string expectedResult)
        {
            var result = str.AsSpan().LeftPart(needle).ToString();
            Assert.Equal(expectedResult, result);
        }

        [Theory]
        [InlineData("", 'q', "")]
        [InlineData("Banana split", ' ', "split")]
        [InlineData("Banana split", 'q', "Banana split")]
        [InlineData("Banana split.", '.', "")]
        [InlineData("Banana split 2", ' ', "2")]
        public void RightPart_ValidArgsCharNeedle_Correct(string str, char needle, string expectedResult)
        {
            var result = str.AsSpan().RightPart(needle).ToString();
            Assert.Equal(expectedResult, result);
        }
    }
}
