using Xunit;
using QuickTranslate.Services;

namespace QuickTranslate.Tests
{
    public class HistoryExporterTests
    {
        [Fact]
        public void EscapeField_EmptyOrNull_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, HistoryExporter.EscapeField("", ","));
            Assert.Equal(string.Empty, HistoryExporter.EscapeField(null!, ","));
        }

        [Fact]
        public void EscapeField_NormalText_ReturnsUnchanged()
        {
            Assert.Equal("hello world", HistoryExporter.EscapeField("hello world", ","));
            Assert.Equal("中文测试", HistoryExporter.EscapeField("中文测试", ","));
            Assert.Equal("abc123", HistoryExporter.EscapeField("abc123", ","));
        }

        [Fact]
        public void EscapeField_ContainsSeparator_WrapsInQuotes()
        {
            var result = HistoryExporter.EscapeField("a,b,c", ",");
            Assert.Equal("\"a,b,c\"", result);
        }

        [Fact]
        public void EscapeField_ContainsNewline_WrapsInQuotes()
        {
            var result = HistoryExporter.EscapeField("line1\nline2", ",");
            Assert.Equal("\"line1\nline2\"", result);
        }

        [Fact]
        public void EscapeField_ContainsDoubleQuote_DoublesQuotesAndWraps()
        {
            var result = HistoryExporter.EscapeField("say \"hello\"", ",");
            Assert.Equal("\"say \"\"hello\"\"\"", result);
        }

        [Fact]
        public void EscapeField_TabSeparator_DetectsTab()
        {
            var result = HistoryExporter.EscapeField("col1\tcol2", "\t");
            Assert.Equal("\"col1\tcol2\"", result);
        }

        // ==================== Formula injection prevention ====================

        [Fact]
        public void EscapeField_StartsWithEquals_Neutralized()
        {
            // =HYPERLINK("...") contains " and , so it gets both neutralized and quoted
            var result = HistoryExporter.EscapeField("=HYPERLINK(\"http://evil.com\")", ",");
            Assert.Contains("\t=HYPERLINK", result);
        }

        [Fact]
        public void EscapeField_StartsWithPlus_Neutralized()
        {
            var result = HistoryExporter.EscapeField("+cmd|' /c calc", ",");
            Assert.StartsWith("\t", result);
            Assert.EndsWith("+cmd|' /c calc", result);
        }

        [Fact]
        public void EscapeField_StartsWithMinus_Neutralized()
        {
            var result = HistoryExporter.EscapeField("-1+1", ",");
            Assert.StartsWith("\t", result);
            Assert.EndsWith("-1+1", result);
        }

        [Fact]
        public void EscapeField_StartsWithAt_Neutralized()
        {
            var result = HistoryExporter.EscapeField("@SUM(A1:A10)", ",");
            Assert.StartsWith("\t", result);
            Assert.EndsWith("@SUM(A1:A10)", result);
        }

        [Fact]
        public void EscapeField_LeadingWhitespaceEquals_Neutralized()
        {
            var result = HistoryExporter.EscapeField("  =CMD|' /c calc", ",");
            Assert.StartsWith("\t", result);
            Assert.EndsWith("  =CMD|' /c calc", result);
        }

        [Fact]
        public void EscapeField_LeadingTabEquals_Neutralized()
        {
            // Tab-prefixed field with " and , gets both neutralized and quoted
            var result = HistoryExporter.EscapeField("\t=HYPERLINK(\"http://evil.com\")", ",");
            Assert.Contains("\t\t=HYPERLINK", result);
        }

        [Fact]
        public void EscapeField_LeadingSpacesPlus_Neutralized()
        {
            var result = HistoryExporter.EscapeField("   +cmd", ",");
            Assert.StartsWith("\t", result);
            Assert.EndsWith("   +cmd", result);
        }

        [Fact]
        public void EscapeField_LeadingSpacesMinus_Neutralized()
        {
            var result = HistoryExporter.EscapeField("\t\t-1+1", ",");
            Assert.StartsWith("\t", result);
        }

        [Fact]
        public void EscapeField_LeadingSpacesAt_Neutralized()
        {
            var result = HistoryExporter.EscapeField("  @SUM(B1:B5)", ",");
            Assert.StartsWith("\t", result);
        }

        [Fact]
        public void EscapeField_PlainText_SkipsNeutralization()
        {
            // Plain text (.txt) exports should NOT neutralize formula injection
            var result = HistoryExporter.EscapeField("=HYPERLINK(\"http://evil.com\")", ",", isPlainText: true);
            Assert.DoesNotContain("\t", result);
        }

        [Fact]
        public void EscapeField_PlainText_NormalFieldsUnchanged()
        {
            Assert.Equal("hello", HistoryExporter.EscapeField("hello", ",", isPlainText: true));
        }

        // ==================== IsFormulaInjectionCandidate ====================

        [Theory]
        [InlineData("=HYPERLINK")]
        [InlineData("+cmd")]
        [InlineData("-1+1")]
        [InlineData("@SUM")]
        [InlineData("  =calc")]
        [InlineData("\t=calc")]
        [InlineData("   +10")]
        public void IsFormulaInjectionCandidate_DangerousStart_ReturnsTrue(string input)
        {
            Assert.True(HistoryExporter.IsFormulaInjectionCandidate(input));
        }

        [Theory]
        [InlineData("")]
        [InlineData("hello")]
        [InlineData("123")]
        [InlineData("  hello")]
        [InlineData("\thello")]
        [InlineData("abc=formula")]
        [InlineData("+abc")]  // Starts with + but followed by letters: + is still dangerous
        public void IsFormulaInjectionCandidate_SafeStart_ReturnsExpected(string input)
        {
            if (string.IsNullOrEmpty(input) || (!input.TrimStart().StartsWith("=") &&
                 !input.TrimStart().StartsWith("+") &&
                 !input.TrimStart().StartsWith("-") &&
                 !input.TrimStart().StartsWith("@")))
            {
                Assert.False(HistoryExporter.IsFormulaInjectionCandidate(input));
            }
            else
            {
                Assert.True(HistoryExporter.IsFormulaInjectionCandidate(input));
            }
        }

        [Fact]
        public void IsFormulaInjectionCandidate_OnlyWhitespace_ReturnsFalse()
        {
            Assert.False(HistoryExporter.IsFormulaInjectionCandidate("   "));
            Assert.False(HistoryExporter.IsFormulaInjectionCandidate("\t\t"));
        }

        // ==================== Combined formula + quoting ====================

        [Fact]
        public void EscapeField_FormulaWithComma_NeutralizedAndQuoted()
        {
            // =SUM(1,2) starts with = AND contains a comma → neutralized then quoted
            var result = HistoryExporter.EscapeField("=SUM(1,2)", ",");
            Assert.Contains("\t=SUM(1,2)", result);        // Neutralization tab inside quotes
            Assert.StartsWith("\"", result);                // Surrounded by quotes
        }

        [Fact]
        public void EscapeField_FormulaWithNewline_NeutralizedAndQuoted()
        {
            var result = HistoryExporter.EscapeField("=A1\nB1", ",");
            Assert.Contains("\t=A1\nB1", result);           // Neutralization tab inside quotes
            Assert.StartsWith("\"", result);                // Surrounded by quotes
        }

        // ==================== Unicode ====================

        [Fact]
        public void EscapeField_UnicodeText_PreservesCharacters()
        {
            Assert.Equal("こんにちは", HistoryExporter.EscapeField("こんにちは", ","));
            Assert.Equal("emoji😀test", HistoryExporter.EscapeField("emoji😀test", ","));
        }

        // ==================== NeutralizeFormulaInjection ====================

        [Fact]
        public void NeutralizeFormulaInjection_PrependsTab()
        {
            Assert.Equal("\t=HYPERLINK", HistoryExporter.NeutralizeFormulaInjection("=HYPERLINK"));
            Assert.Equal("\t+cmd", HistoryExporter.NeutralizeFormulaInjection("+cmd"));
            Assert.Equal("\t-SUM", HistoryExporter.NeutralizeFormulaInjection("-SUM"));
            Assert.Equal("\t@REF", HistoryExporter.NeutralizeFormulaInjection("@REF"));
        }

        [Fact]
        public void NeutralizeFormulaInjection_PreservesLeadingWhitespace()
        {
            var result = HistoryExporter.NeutralizeFormulaInjection("   =calc");
            Assert.Equal("\t   =calc", result);
        }
    }
}
