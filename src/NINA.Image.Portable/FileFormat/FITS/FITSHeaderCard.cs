namespace NINA.Image.FileFormat.FITS;

public record FITSHeaderCard(string Keyword, string Value, string? Comment = null) {
    public const int CARD_LENGTH = 80;
    public const int KEYWORD_LENGTH = 8;

    public static FITSHeaderCard? Parse(ReadOnlySpan<byte> card) {
        if (card.Length < CARD_LENGTH) return null;

        var keyword = System.Text.Encoding.ASCII.GetString(card[..KEYWORD_LENGTH]).TrimEnd();
        if (keyword == "END") return new FITSHeaderCard("END", "");

        if (card[8] != '=' || card[9] != ' ') {
            return new FITSHeaderCard(keyword, "", System.Text.Encoding.ASCII.GetString(card[8..]).Trim());
        }

        var valueComment = System.Text.Encoding.ASCII.GetString(card[10..]).TrimEnd();
        string value;
        string? comment = null;

        if (valueComment.StartsWith('\'')) {
            int endQuote = valueComment.IndexOf('\'', 1);
            if (endQuote > 0) {
                value = valueComment[1..endQuote].TrimEnd();
                int slashPos = valueComment.IndexOf('/', endQuote);
                if (slashPos >= 0) comment = valueComment[(slashPos + 1)..].Trim();
            } else {
                value = valueComment[1..].TrimEnd();
            }
        } else {
            int slashPos = valueComment.IndexOf('/');
            if (slashPos >= 0) {
                value = valueComment[..slashPos].Trim();
                comment = valueComment[(slashPos + 1)..].Trim();
            } else {
                value = valueComment.Trim();
            }
        }

        return new FITSHeaderCard(keyword, value, comment);
    }
}
