using System.Windows;
using System.Windows.Controls;

namespace TerminalV.Gateway;

internal static class TailnetPairingPrompt
{
    public static async Task<bool> ShowAsync(Window owner, TailnetIdentity identity, CancellationToken ct)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? dialog = null;
        await owner.Dispatcher.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) { result.TrySetResult(false); return; }
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = $"Разрешить доступ к терминалам этого ПК?\n\nАккаунт: {identity.Login}\nУстройство: {identity.Device}\n\nТелефон сможет читать вывод, вводить команды и создавать или закрывать сессии от вашего имени.", TextWrapping = TextWrapping.Wrap });
            var deny = new Button { Content = "Отклонить", Margin = new Thickness(0, 16, 0, 8), Padding = new Thickness(12), IsCancel = true };
            var allow = new Button { Content = "Разрешить этому устройству", Padding = new Thickness(12) };
            panel.Children.Add(deny); panel.Children.Add(allow);
            dialog = new Window { Owner = owner, Title = "TerminalV — подключение телефона", Content = panel, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            deny.Click += (_, _) => dialog.Close();
            allow.Click += (_, _) => { result.TrySetResult(!ct.IsCancellationRequested); dialog.Close(); };
            dialog.Closed += (_, _) => result.TrySetResult(false);
            dialog.Show();
            deny.Focus();
        });
        using var registration = ct.Register(() => owner.Dispatcher.BeginInvoke(new Action(() => { dialog?.Close(); result.TrySetResult(false); })));
        return await result.Task;
    }
}
