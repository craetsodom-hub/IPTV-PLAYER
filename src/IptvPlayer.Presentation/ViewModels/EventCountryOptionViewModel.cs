namespace IptvPlayer.Presentation.ViewModels;

public sealed class EventCountryOptionViewModel
{
    public EventCountryOptionViewModel(string code, string label)
    {
        Code = code;
        Label = label;
    }

    public string Code { get; }

    public string Label { get; }

    public string DisplayLabel => Label;

    public string FlagImageUri
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Code) || Code.Length != 2)
            {
                return "https://raw.githubusercontent.com/twitter/twemoji/master/assets/72x72/1f310.png";
            }

            var upperCode = Code.ToUpperInvariant();
            foreach (var character in upperCode)
            {
                if (!char.IsLetter(character))
                {
                    return "https://raw.githubusercontent.com/twitter/twemoji/master/assets/72x72/1f310.png";
                }
            }

            return $"https://flagcdn.com/w40/{upperCode.ToLowerInvariant()}.png";
        }
    }

    public override string ToString() => DisplayLabel;
}
