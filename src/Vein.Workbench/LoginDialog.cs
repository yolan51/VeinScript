using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Cloud;
using Vein.Compiler.Project;

namespace Vein.Workbench;

// Signing in to the VeinScript service.
//
// TWO WAYS IN, AND THE TOKEN IS THE DEFAULT. The account page on the website issues a bearer token
// meant for exactly this, and pasting it is better than typing a password into a desktop application:
// nothing secret is stored that could be reused elsewhere, the token is visible on the site, and it is
// revoked by re-issuing it there. Email and password stay available because someone who has not opened
// the site yet still has to get in somehow — but it is the second tab, not the first.
//
// THE HANDLE IS ASKED FOR HERE because it becomes the first segment of every published file name:
// `alice.Combat.shards.Boot.vein`. It is not the display name — that may be "Jane Doe", spaces and
// all — so the field is validated live against the same rule `VeinNames` enforces, rather than failing
// at the moment somebody tries to publish.
//
// Built in code rather than XAML, like every other dialog here.
internal sealed class LoginDialog : Window
{
    private readonly TabControl _how = new();
    private readonly TextBox _token = new() { Watermark = "eyJhbGciOi…", AcceptsReturn = false };
    private readonly TextBox _email = new() { Watermark = "you@example.com" };
    private readonly TextBox _password = new() { PasswordChar = '•' };
    private readonly TextBox _handle = new() { Watermark = "yolan" };
    private readonly TextBox _name = new() { Watermark = "Yolan" };
    private readonly TextBlock _error = new()
    {
        Foreground = new SolidColorBrush(Color.Parse("#F48771")),
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false
    };
    private readonly TextBlock _handleHint = new()
    {
        Foreground = new SolidColorBrush(Color.Parse("#8A8A8A")),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button _accept = new() { Content = "Sign in", IsDefault = true };

    private CloudSession? _result;
    private bool _busy;

    private LoginDialog(CloudSession? existing)
    {
        Title = "Sign in to VeinScript";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _handle.Text = existing?.Handle ?? "";
        _name.Text = existing?.FullName ?? "";
        _email.Text = existing?.Email ?? "";

        _handle.TextChanged += (_, _) => ValidateHandle();
        _name.TextChanged += (_, _) => { if (_handle.Text is null or "") ValidateHandle(); };

        foreach (var box in new[] { _token, _email, _password, _handle, _name })
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };

        _how.Items.Add(new TabItem { Header = "Access token", Content = TokenPage() });
        _how.Items.Add(new TabItem { Header = "Email", Content = PasswordPage() });

        _accept.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        var site = new Button { Content = "Open account page…" };
        site.Click += (_, _) => OpenSite();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children =
            {
                _how,
                new Separator(),
                new TextBlock
                {
                    Text = "Publishing handle",
                    FontWeight = FontWeight.SemiBold
                },
                _handle,
                _handleHint,
                _error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        site,
                        new Panel { Width = 1 },
                        _accept,
                        cancel
                    }
                }
            }
        };

        ValidateHandle();
        Opened += (_, _) => _token.Focus();
    }

    private Control TokenPage() => new StackPanel
    {
        Margin = new Avalonia.Thickness(12),
        Spacing = 8,
        Children =
        {
            new TextBlock
            {
                Text = "Paste the access token from your account page on the VeinScript site. " +
                       "Your password is never sent to the Workbench, and you can revoke the token " +
                       "by refreshing it there.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#B0B0B0")),
                FontSize = 12
            },
            _token,
            new TextBlock { Text = "Display name (optional)", FontSize = 12 },
            _name
        }
    };

    private Control PasswordPage() => new StackPanel
    {
        Margin = new Avalonia.Thickness(12),
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = "Email", FontSize = 12 },
            _email,
            new TextBlock { Text = "Password", FontSize = 12 },
            _password
        }
    };

    /// The handle rule is the one `VeinNames` enforces, checked as you type rather than at publish
    /// time — the field explains what it becomes, so the constraint reads as a reason and not a
    /// restriction.
    private bool ValidateHandle()
    {
        string handle = Handle();

        if (handle.Length == 0)
        {
            _handleHint.Text = "Becomes the first part of every file you publish.";
            return false;
        }

        if (VeinNames.ToName(handle, "Bundle", "Bundle.vein") is null)
        {
            _handleHint.Text = "Letters, digits, - and _ only — no spaces or dots.";
            return false;
        }

        _handleHint.Text = $"Your files will publish as  {handle}.Combat.shards.Boot.vein";
        return true;
    }

    /// What was typed, or a slug of the display name when the field was left alone.
    private string Handle()
    {
        string typed = (_handle.Text ?? "").Trim();
        if (typed.Length > 0) return typed;

        string from = (_name.Text ?? "").Trim();
        if (from.Length == 0) from = (_email.Text ?? "").Trim();
        return CloudSession.Slug(from);
    }

    private async void Accept()
    {
        if (_busy) return;

        string handle = Handle();
        if (!ValidateHandle())
        {
            Fail(handle.Length == 0
                ? "A publishing handle is needed — it becomes part of every file name."
                : "That handle cannot be part of a file name. Use letters, digits, - or _.");
            return;
        }

        Busy(true);
        try
        {
            bool byToken = _how.SelectedIndex == 0;

            _result = byToken
                ? await AuthApi.FromTokenAsync((_token.Text ?? "").Trim(), handle, (_name.Text ?? "").Trim())
                : (await AuthApi.LoginAsync((_email.Text ?? "").Trim(), _password.Text ?? "")) with { Handle = handle };

            Close();
        }
        catch (CloudException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            // Offline, DNS, a proxy in the way. Worth distinguishing from "the service said no".
            Fail("Could not reach the service — " + ex.Message);
        }
        finally { Busy(false); }
    }

    private void Busy(bool busy)
    {
        _busy = busy;
        _accept.Content = busy ? "Signing in…" : "Sign in";
        _accept.IsEnabled = !busy;
        _how.IsEnabled = !busy;
    }

    private void Fail(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    private void OpenSite()
    {
        try
        {
            Process.Start(new ProcessStartInfo(CloudConfig.Current.SignupUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Fail("Could not open a browser: " + ex.Message);
        }
    }

    /// Null when cancelled, or when the dialog was closed without a session.
    public static async Task<CloudSession?> ShowAsync(Window owner, CloudSession? existing = null)
    {
        var dialog = new LoginDialog(existing);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
