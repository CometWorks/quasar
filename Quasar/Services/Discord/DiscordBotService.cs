using Discord;
using Discord.WebSocket;

namespace Quasar.Services.Discord;

public sealed class DiscordBotService : IHostedService, IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private readonly DiscordOptionsCatalog _optionsCatalog;
    private readonly AgentRegistry _registry;
    private readonly DiscordCommandRouter _commandRouter;
    private readonly DiscordChatRelayService _chatRelayService;
    private readonly DiscordDeathRelayService _deathRelayService;
    private readonly DiscordLogRelayService _logRelayService;
    private readonly DiscordAnalyticsExportService _analyticsExportService;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly CancellationTokenSource _shutdown = new();

    private DiscordSocketClient? _client;
    private CancellationTokenSource? _botLifetime;
    private int _disposed;
    private string _stateText = "Stopped";
    private string _lastError = string.Empty;

    public DiscordBotService(
        DiscordOptionsCatalog optionsCatalog,
        AgentRegistry registry,
        DiscordCommandRouter commandRouter,
        DiscordChatRelayService chatRelayService,
        DiscordDeathRelayService deathRelayService,
        DiscordLogRelayService logRelayService,
        DiscordAnalyticsExportService analyticsExportService,
        ILogger<DiscordBotService> logger)
    {
        _optionsCatalog = optionsCatalog;
        _registry = registry;
        _commandRouter = commandRouter;
        _chatRelayService = chatRelayService;
        _deathRelayService = deathRelayService;
        _logRelayService = logRelayService;
        _analyticsExportService = analyticsExportService;
        _logger = logger;
    }

    public event Action? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _optionsCatalog.Changed += HandleOptionsChanged;
        _registry.Changed += HandleRegistryChanged;
        return TryRestartBotAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _optionsCatalog.Changed -= HandleOptionsChanged;
        _registry.Changed -= HandleRegistryChanged;
        _shutdown.Cancel();
        await StopBotCoreAsync(cancellationToken);
        SetState("Stopped", string.Empty);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _shutdown.Dispose();
        _restartGate.Dispose();
        _botLifetime?.Dispose();
    }

    public DiscordBotStatusSnapshot GetStatus()
    {
        var options = _optionsCatalog.GetOptions();
        DiscordSocketClient? client;
        string stateText;
        string lastError;

        lock (_sync)
        {
            client = _client;
            stateText = _stateText;
            lastError = _lastError;
        }

        return new DiscordBotStatusSnapshot
        {
            Enabled = options.Enabled,
            TokenConfigured = !string.IsNullOrWhiteSpace(options.BotToken),
            GuildConfigured = options.GuildId != 0,
            StateText = client is null ? stateText : client.ConnectionState.ToString(),
            LastError = lastError,
            IsRunning = client is not null,
        };
    }

    private void HandleOptionsChanged()
    {
        _ = Task.Run(() => TryRestartBotAsync(CancellationToken.None), CancellationToken.None);
    }

    private void HandleRegistryChanged()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var client = GetClient();
                var botLifetimeToken = GetBotLifetimeToken();
                if (client is null || botLifetimeToken is null)
                    return;

                var options = _optionsCatalog.GetOptions();
                await _chatRelayService.HandleChangedAsync(client, options, botLifetimeToken.Value);
                await _deathRelayService.HandleChangedAsync(client, options, botLifetimeToken.Value);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Discord chat relay dispatch failed.");
            }
        }, CancellationToken.None);
    }

    private async Task TryRestartBotAsync(CancellationToken cancellationToken)
    {
        await _restartGate.WaitAsync(cancellationToken);

        try
        {
            await StopBotCoreAsync(cancellationToken);

            var options = _optionsCatalog.GetOptions();
            if (!options.Enabled)
            {
                SetState("Disabled", string.Empty);
                return;
            }

            if (string.IsNullOrWhiteSpace(options.BotToken) || options.GuildId == 0)
            {
                SetState("NotConfigured", "Bot token and guild ID required.");
                return;
            }

            var client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
            });

            var botLifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            client.Log += HandleClientLogAsync;
            client.MessageReceived += _commandRouter.HandleAsync;

            try
            {
                SetState("Starting", string.Empty);
                await client.LoginAsync(TokenType.Bot, options.BotToken);
                await client.StartAsync();

                lock (_sync)
                {
                    _client = client;
                    _botLifetime = botLifetime;
                }

                _chatRelayService.Reset();
                _deathRelayService.Reset();
                _logRelayService.Reset();
                _analyticsExportService.Reset();

                await _logRelayService.StartAsync(client, options, botLifetime.Token);
                await _analyticsExportService.StartAsync(client, options, botLifetime.Token);
                await _chatRelayService.HandleChangedAsync(client, options, botLifetime.Token);

                SetState("Running", string.Empty);
            }
            catch (Exception exception)
            {
                botLifetime.Cancel();
                client.MessageReceived -= _commandRouter.HandleAsync;
                client.Log -= HandleClientLogAsync;

                try
                {
                    await client.StopAsync();
                    await client.LogoutAsync();
                }
                catch
                {
                }

                client.Dispose();
                botLifetime.Dispose();

                _logger.LogError(exception, "Failed starting Discord bot.");
                SetState("Faulted", exception.Message);
            }
        }
        finally
        {
            _restartGate.Release();
        }
    }

    private async Task StopBotCoreAsync(CancellationToken cancellationToken)
    {
        DiscordSocketClient? client;
        CancellationTokenSource? botLifetime;

        lock (_sync)
        {
            client = _client;
            botLifetime = _botLifetime;
            _client = null;
            _botLifetime = null;
        }

        botLifetime?.Cancel();
        _chatRelayService.Reset();
        _deathRelayService.Reset();
        _logRelayService.Reset();
        _analyticsExportService.Reset();

        if (client is null)
        {
            botLifetime?.Dispose();
            return;
        }

        client.MessageReceived -= _commandRouter.HandleAsync;
        client.Log -= HandleClientLogAsync;

        try
        {
            await client.StopAsync();
            await client.LogoutAsync();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Discord bot stop encountered an error.");
        }
        finally
        {
            client.Dispose();
            botLifetime?.Dispose();
        }
    }

    private DiscordSocketClient? GetClient()
    {
        lock (_sync)
        {
            return _client;
        }
    }

    private CancellationToken? GetBotLifetimeToken()
    {
        lock (_sync)
        {
            return _botLifetime?.Token;
        }
    }

    private Task HandleClientLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Debug,
            _ => LogLevel.Information,
        };

        _logger.Log(level, message.Exception, "Discord.Net {Source}: {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }

    private void SetState(string stateText, string lastError)
    {
        lock (_sync)
        {
            _stateText = stateText;
            _lastError = lastError;
        }

        Changed?.Invoke();
    }
}

public sealed class DiscordBotStatusSnapshot
{
    public bool Enabled { get; init; }

    public bool TokenConfigured { get; init; }

    public bool GuildConfigured { get; init; }

    public bool IsRunning { get; init; }

    public string StateText { get; init; } = string.Empty;

    public string LastError { get; init; } = string.Empty;
}
