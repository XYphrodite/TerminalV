namespace TerminalV.Data;

internal sealed class LaunchProfile
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Shell { get; set; } = "auto";
    public string? Cwd { get; set; }
    public string? Color { get; set; }
    public string? StartupCommand { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 80 ||
            string.IsNullOrWhiteSpace(Title) || Title.Length > 80 || Title.Any(char.IsControl))
            throw new ArgumentException("Укажите название профиля до 80 символов.");
        if (Shell is not ("auto" or "powershell" or "pwsh" or "cmd"))
            throw new ArgumentException("Неизвестная оболочка профиля.");
        if (Color is not (null or "" or "blue" or "green" or "amber" or "rose" or "violet"))
            throw new ArgumentException("Неизвестная цветовая метка.");
        if (Cwd is { Length: > 2048 } || Cwd?.Any(char.IsControl) == true)
            throw new ArgumentException("Некорректная папка профиля.");
        if (StartupCommand is { Length: > 4096 } || StartupCommand?.Contains('\0') == true)
            throw new ArgumentException("Стартовая команда: максимум 4096 символов, без NUL.");
        if (Shell == "cmd" && StartupCommand?.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Для cmd укажите команду в одну строку.");
    }
}
