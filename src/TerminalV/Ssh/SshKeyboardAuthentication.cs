using Renci.SshNet;
using Renci.SshNet.Common;

namespace TerminalV.Ssh;

internal static class SshKeyboardAuthentication
{
    public static KeyboardInteractiveAuthenticationMethod Create(string username, string password)
    {
        var method = new KeyboardInteractiveAuthenticationMethod(username);
        method.AuthenticationPrompt += (_, args) =>
        {
            // A password supplied in connection settings is not an OTP or a
            // response to an arbitrary challenge. Do not send it to those prompts.
            if (args.Prompts.Any(p => p.IsEchoed || !IsPasswordPrompt(p.Request)))
                throw new SshAuthenticationException("SSH-сервер запросил дополнительное подтверждение входа. Вход по коду или через браузер пока не поддерживается.");
            foreach (var prompt in args.Prompts) prompt.Response = password;
        };
        return method;
    }

    private static bool IsPasswordPrompt(string request)
    {
        var text = request.Trim();
        return text.Equals("Password:", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Password", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Пароль:", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Пароль", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith("'s password:", StringComparison.OrdinalIgnoreCase);
    }
}
