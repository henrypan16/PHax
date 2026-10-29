using System.ComponentModel;
using PHax.App.Services;
using PHax.App.ViewModels;
using PHax.Core.Ipc;
using PHax.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PHax.App.Views;

public sealed partial class IncomingPage : Page
{
    public ShellViewModel Shell { get; } = App.GetService<ShellViewModel>();
    public InboxViewModel History { get; }

    public IncomingPage()
    {
        History = ActivatorUtilities.CreateInstance<InboxViewModel>(AppServices);
        History.IncludeIncomplete = true;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            Shell.PropertyChanged += OnShellChanged;
            Render();
            await History.RefreshAsync();
        };
        Unloaded += (_, _) => Shell.PropertyChanged -= OnShellChanged;
    }

    private static IServiceProvider AppServices => App.GetService<IServiceProvider>();

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Status)) Render();
    }

    private void Render()
    {
        var status = Shell.Status;
        var call = status?.Call;
        if (call is null)
        {
            CallState.Text = status?.ModemState == ModemState.Ready ? "Waiting for calls" : "No active call";
            CallDetail.Text = status is null ? "Service not connected." : $"Modem: {status.ModemState}.";
            CallProgress.Visibility = Visibility.Collapsed;
            AnswerButton.IsEnabled = false;
            return;
        }
        bool ringing = status!.ModemState == ModemState.Ringing;
        CallState.Text = ringing ? $"Incoming call — ring {call.RingCount}" : $"Receiving fax — {call.State}";
        var parts = new List<string>();
        if (call.CallerNumber is not null) parts.Add($"Caller: {call.CallerNumber}");
        if (call.CallerName is not null) parts.Add(call.CallerName);
        if (call.RemoteId is not null) parts.Add($"Remote ID: {call.RemoteId}");
        if (!ringing)
        {
            parts.Add($"Pages received: {call.PagesReceived}");
            parts.Add($"Phase {status.Phase}");
            if (status.Modulation is not null) parts.Add(status.Modulation);
            if (status.Resolution is not null) parts.Add(status.Resolution.ToString()!);
        }
        if (ringing && call.AwaitingManualAnswer) parts.Add("Automatic reception is off — click Answer to receive.");
        CallDetail.Text = string.Join(" · ", parts);
        CallProgress.Visibility = ringing ? Visibility.Collapsed : Visibility.Visible;
        AnswerButton.IsEnabled = ringing;
        if (status.ModemState == ModemState.Ready) _ = History.RefreshAsync();
    }

    private async void Answer_Click(object sender, RoutedEventArgs e) =>
        await App.GetService<ServiceClient>().TrySendAsync(PipeCommands.AnswerCall);

    private async void Simulate_Click(object sender, RoutedEventArgs e) =>
        await App.GetService<ServiceClient>().TrySendAsync("simulateIncoming");
}
