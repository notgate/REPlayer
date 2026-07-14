using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ReVM.Automation;

namespace ReVM;

public partial class AgentSetupDialog : Window
{
    private readonly AiAgentProfileStore _store;
    private readonly IAiAgentProviderClientFactory _providers;
    private readonly ObservableCollection<AiAgentProfile> _profiles = new();
    private bool _loading;
    private bool _newProfile;
    private string? _pendingDeleteId;

    public AgentSetupDialog(
        AiAgentProfileStore store,
        IAiAgentProviderClientFactory providers,
        string? selectedAgentId = null)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        ProviderCombo.ItemsSource = AgentProviderCatalog.All.Select(provider => new ProviderOption(provider, AgentProviderCatalog.DisplayName(provider))).ToArray();
        ProfileList.ItemsSource = _profiles;
        ReloadProfiles(selectedAgentId);
    }

    public string? SelectedAgentId { get; private set; }

    private void ReloadProfiles(string? selectedAgentId)
    {
        _loading = true;
        try
        {
            _profiles.Clear();
            foreach (var profile in _store.Load()) _profiles.Add(profile);
            var selected = _profiles.FirstOrDefault(profile => string.Equals(profile.Id, selectedAgentId, StringComparison.OrdinalIgnoreCase)) ?? _profiles.FirstOrDefault();
            ProfileList.SelectedItem = selected;
            if (selected is null) BeginNewProfile();
            else LoadProfile(selected);
        }
        finally
        {
            _loading = false;
        }
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileList.SelectedItem is not AiAgentProfile profile) return;
        LoadProfile(profile);
    }

    private void LoadProfile(AiAgentProfile profile)
    {
        _loading = true;
        try
        {
            _newProfile = false;
            _pendingDeleteId = null;
            SelectedAgentId = profile.Id;
            AgentIdBox.Text = profile.Id;
            AgentIdBox.IsReadOnly = true;
            AgentNameBox.Text = profile.Name;
            SelectProvider(profile.Provider);
            ModelBox.Text = profile.Model;
            EndpointBox.Text = profile.BaseUrl;
            ApiKeyBox.Clear();
            CredentialStatusText.Text = _store.HasCredential(profile.Id)
                ? "Key stored in Windows Credential Manager. Leave this field blank to keep it."
                : "No key is stored. Enter one before testing or running tasks.";
            DeleteButton.IsEnabled = true;
            StatusText.Text = "Editing " + profile.Name;
        }
        finally
        {
            _loading = false;
        }
    }

    private void NewAgent_Click(object sender, RoutedEventArgs e) => BeginNewProfile();

    private void BeginNewProfile()
    {
        _loading = true;
        try
        {
            _newProfile = true;
            _pendingDeleteId = null;
            ProfileList.SelectedItem = null;
            SelectedAgentId = null;
            var sequence = 1;
            var id = "agent-" + sequence.ToString(CultureInfo.InvariantCulture);
            while (_profiles.Any(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                sequence++;
                id = "agent-" + sequence.ToString(CultureInfo.InvariantCulture);
            }
            AgentIdBox.IsReadOnly = false;
            AgentIdBox.Text = id;
            AgentNameBox.Text = "Analysis agent " + sequence.ToString(CultureInfo.InvariantCulture);
            SelectProvider(AiAgentProviderKind.OpenRouter);
            ModelBox.Text = AgentProviderCatalog.DefaultModel(AiAgentProviderKind.OpenRouter);
            EndpointBox.Text = AgentProviderCatalog.DefaultBaseUrl(AiAgentProviderKind.OpenRouter);
            ApiKeyBox.Clear();
            CredentialStatusText.Text = "Enter this agent's API key. It will be saved only in Windows Credential Manager.";
            DeleteButton.IsEnabled = false;
            StatusText.Text = "New agent profile";
        }
        finally
        {
            _loading = false;
        }
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderCombo.SelectedItem is not ProviderOption option) return;
        ModelBox.Text = AgentProviderCatalog.DefaultModel(option.Provider);
        EndpointBox.Text = AgentProviderCatalog.DefaultBaseUrl(option.Provider);
        StatusText.Text = AgentProviderCatalog.DisplayName(option.Provider) + " defaults applied";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = BuildProfile();
            _store.Save(profile, string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? null : ApiKeyBox.Password);
            SelectedAgentId = profile.Id;
            ApiKeyBox.Clear();
            ReloadProfiles(profile.Id);
            StatusText.Text = "Saved " + profile.Name;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        try
        {
            var profile = BuildProfile();
            var key = string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? _store.ReadCredential(profile.Id) : ApiKeyBox.Password;
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Enter an API key or save one for this agent first.");
            StatusText.Text = $"Testing {profile.ProviderDisplayName} / {profile.Model}...";
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var turn = await _providers.Create(profile.Provider).CompleteAsync(profile, key,
                new[]
                {
                    new AiAgentConversationMessage { Role = "system", Content = "This is a connection test. Do not call tools." },
                    new AiAgentConversationMessage { Role = "user", Content = "Reply with REPLAYER_AGENT_READY and no other text." },
                }, timeout.Token);
            if (turn.Assistant.ToolCalls.Count > 0 || string.IsNullOrWhiteSpace(turn.Assistant.Content))
                throw new InvalidOperationException("Provider connected but did not return a text response.");
            StatusText.Text = "Connection passed: " + Summarize(turn.Assistant.Content);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Connection test timed out.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_newProfile || ProfileList.SelectedItem is not AiAgentProfile profile) return;
        if (!string.Equals(_pendingDeleteId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            _pendingDeleteId = profile.Id;
            StatusText.Text = "Click Delete again to remove this profile and its stored key.";
            return;
        }
        try
        {
            _store.Delete(profile.Id);
            SelectedAgentId = null;
            ReloadProfiles(null);
            StatusText.Text = "Agent profile deleted.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private AiAgentProfile BuildProfile()
    {
        if (ProviderCombo.SelectedItem is not ProviderOption provider) throw new InvalidOperationException("Select a provider.");
        if (!int.TryParse("12", NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximumTurns)) maximumTurns = 12;
        return new AiAgentProfile
        {
            Id = AgentIdBox.Text.Trim(),
            Name = AgentNameBox.Text.Trim(),
            Provider = provider.Provider,
            Model = ModelBox.Text.Trim(),
            BaseUrl = EndpointBox.Text.Trim(),
            MaximumTurns = maximumTurns,
        };
    }

    private void SelectProvider(AiAgentProviderKind provider)
    {
        ProviderCombo.SelectedItem = ProviderCombo.Items.OfType<ProviderOption>().FirstOrDefault(option => option.Provider == provider);
    }

    private static string Summarize(string value)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 100 ? compact : compact[..97] + "...";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record ProviderOption(AiAgentProviderKind Provider, string Name);
}
