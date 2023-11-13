using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetPad.Common;
using NetPad.Data;
using NetPad.DotNet;
using NetPad.Events;
using NetPad.IO;
using NetPad.Runtimes;

namespace NetPad.Scripts;

public class ScriptEnvironment : IDisposable, IAsyncDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ILogger<ScriptEnvironment> _logger;
    private readonly IDataConnectionResourcesCache _dataConnectionResourcesCache;
    private IServiceScope _serviceScope;
    private IInputReader<string> _inputReader;
    private IOutputWriter<object> _outputAdapter;
    private ScriptStatus _status;
    private IScriptRuntime? _runtime;
    private bool _isDisposed;

    public ScriptEnvironment(Script script, IServiceScope serviceScope)
    {
        Script = script;
        _serviceScope = serviceScope;
        _eventBus = _serviceScope.ServiceProvider.GetRequiredService<IEventBus>();
        _dataConnectionResourcesCache = _serviceScope.ServiceProvider.GetRequiredService<IDataConnectionResourcesCache>();
        _logger = _serviceScope.ServiceProvider.GetRequiredService<ILogger<ScriptEnvironment>>();
        _inputReader = ActionInputReader<string>.Null;
        _outputAdapter = ActionOutputWriter<object>.Null;
        _status = ScriptStatus.Ready;

        Initialize();
    }

    public Script Script { get; }

    public virtual ScriptStatus Status => _status;

    public double RunDurationMilliseconds { get; private set; }

    public async Task RunAsync(RunOptions runOptions)
    {
        EnsureNotDisposed();

        _logger.LogTrace($"{nameof(RunAsync)} start");

        if (_status.NotIn(ScriptStatus.Ready, ScriptStatus.Error))
        {
            throw new InvalidOperationException($"Script is not in the correct state to run. Status is currently: {_status}");
        }

        await SetStatusAsync(ScriptStatus.Running);

        try
        {
            if (Script.DataConnection != null)
            {
                await AppendDataConnectionResourcesAsync(runOptions, Script.DataConnection);
            }

            // Script could have been requested to stop by this point
            if (Status == ScriptStatus.Stopping) return;

            var runtime = await GetRuntimeAsync();

            if (Status == ScriptStatus.Stopping) return;

            var runResult = await runtime.RunScriptAsync(runOptions);

            await SetRunDurationAsync(runResult.DurationMs);

            await SetStatusAsync(runResult.IsScriptCompletedSuccessfully || runResult.IsRunCancelled ? ScriptStatus.Ready : ScriptStatus.Error);

            _logger.LogDebug("Run finished with status: {Status}", _status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running script");
            await _outputAdapter.WriteAsync(new ErrorScriptOutput(ex));
            await SetStatusAsync(ScriptStatus.Error);
        }
        finally
        {
            _logger.LogTrace($"{nameof(RunAsync)} end");
        }
    }

    private async Task AppendDataConnectionResourcesAsync(RunOptions runOptions, DataConnection dataConnection)
    {
        var connectionCode = await _dataConnectionResourcesCache.GetSourceGeneratedCodeAsync(dataConnection, Script.Config.TargetFrameworkVersion);
        if (connectionCode.ApplicationCode.Any())
        {
            runOptions.AdditionalCode.AddRange(connectionCode.ApplicationCode);
        }

        if (dataConnection.Type == DataConnectionType.MSSQLServer)
        {
            // Special case for MS SQL Server. When targeting a MS SQL server database, we must load the
            // os-specific version of Microsoft.Data.SqlClient.dll that MSBuild copies for us in
            // a specific dir (in app .csproj file). This behavior is an issue where .NET does not
            // properly detect platform-specific version of the assembly
            // See:
            // https://github.com/dotnet/SqlClient/issues/1631
            // https://github.com/dotnet/SqlClient/issues/1643
            var appExePath = Assembly.GetEntryAssembly()?.Location;
            if (appExePath != null && File.Exists(appExePath))
            {
                string sqlClientVersion = Script.Config.TargetFrameworkVersion == DotNetFrameworkVersion.DotNet6
                    ? "2.1.4"
                    : "5.0.1";

                string os = PlatformUtil.IsWindowsPlatform() ? "win" : "unix";

                runOptions.Assets.Add(new RunAsset(Path.Combine(
                        Path.GetDirectoryName(appExePath)!,
                        $"Assets/Assemblies/Microsoft.Data.SqlClient/{sqlClientVersion}/{os}/Microsoft.Data.SqlClient.dll"),
                    "./Microsoft.Data.SqlClient.dll"));

                // Windows also needs Microsoft.Data.SqlClient.SNI
                if (PlatformUtil.IsWindowsPlatform())
                {
                    string sqlClientSniVersion = Script.Config.TargetFrameworkVersion == DotNetFrameworkVersion.DotNet6
                        ? "2.1.1"
                        : "5.0.1";

                    runOptions.Assets.Add(new RunAsset(Path.Combine(
                            Path.GetDirectoryName(appExePath)!,
                            $"Assets/Assemblies/Microsoft.Data.SqlClient.SNI/{sqlClientSniVersion}/win-x64/Microsoft.Data.SqlClient.SNI.dll"),
                        "./Microsoft.Data.SqlClient.SNI.dll"));
                }
            }
        }

        var connectionAssembly = await _dataConnectionResourcesCache.GetAssemblyAsync(dataConnection, Script.Config.TargetFrameworkVersion);
        if (connectionAssembly != null)
        {
            runOptions.AdditionalReferences.Add(new AssemblyImageReference(connectionAssembly));
        }

        var requiredReferences = await _dataConnectionResourcesCache.GetRequiredReferencesAsync(dataConnection, Script.Config.TargetFrameworkVersion);

        if (requiredReferences.Any())
        {
            runOptions.AdditionalReferences.AddRange(requiredReferences);
        }
    }

    public async Task StopAsync()
    {
        EnsureNotDisposed();

        _logger.LogTrace($"{nameof(StopAsync)} start");

        if (Status != ScriptStatus.Running)
        {
            return;
        }

        var stopTime = DateTime.Now;
        await SetStatusAsync(ScriptStatus.Stopping);

        try
        {
            // The runtime might not have been initialized yet which means no running is taking place
            if (_runtime != null)
            {
                await _runtime.StopScriptAsync();
            }

            await _outputAdapter.WriteAsync(new RawScriptOutput($"Script stopped at: {stopTime}"));
            await SetStatusAsync(ScriptStatus.Ready);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping script");
            await _outputAdapter.WriteAsync(new ErrorScriptOutput(ex));
            await SetStatusAsync(ScriptStatus.Error);
        }
        finally
        {
            _logger.LogTrace($"{nameof(StopAsync)} end");
        }
    }

    public void SetIO(IInputReader<string> inputReader, IOutputWriter<object> outputAdapter)
    {
        EnsureNotDisposed();

        RemoveScriptRuntimeIOHandlers();

        _inputReader = inputReader ?? throw new ArgumentNullException(nameof(inputReader));
        _outputAdapter = outputAdapter;

        AddScriptRuntimeIOHandlers();
    }

    private void Initialize()
    {
        Script.OnPropertyChanged.Add(async args =>
        {
            await _eventBus.PublishAsync(new ScriptPropertyChangedEvent(Script.Id, args.PropertyName, args.OldValue, args.NewValue));
        });

        Script.Config.OnPropertyChanged.Add(async args =>
        {
            await _eventBus.PublishAsync(
                new ScriptConfigPropertyChangedEvent(Script.Id, args.PropertyName, args.OldValue, args.NewValue));
        });
    }

    private async Task SetStatusAsync(ScriptStatus status)
    {
        var oldValue = _status;
        _status = status;
        await _eventBus.PublishAsync(new EnvironmentPropertyChangedEvent(Script.Id, nameof(Status), oldValue, status));
    }

    private async Task SetRunDurationAsync(double runDurationMs)
    {
        var oldValue = RunDurationMilliseconds;
        RunDurationMilliseconds = runDurationMs;
        await _eventBus.PublishAsync(new EnvironmentPropertyChangedEvent(Script.Id, nameof(RunDurationMilliseconds), oldValue, runDurationMs));
    }

    private async Task<IScriptRuntime> GetRuntimeAsync()
    {
        if (_runtime == null)
        {
            _logger.LogDebug("Initializing new runtime");

            var factory = _serviceScope.ServiceProvider.GetRequiredService<IScriptRuntimeFactory>();
            _runtime = await factory.CreateScriptRuntimeAsync(Script);

            AddScriptRuntimeIOHandlers();
        }

        return _runtime;
    }

    private void AddScriptRuntimeIOHandlers()
    {
        _runtime?.AddInput(_inputReader);
        _runtime?.AddOutput(_outputAdapter);
    }

    private void RemoveScriptRuntimeIOHandlers()
    {
        _runtime?.RemoveInput(_inputReader);
        _runtime?.RemoveOutput(_outputAdapter);
    }

    private void EnsureNotDisposed()
    {
        if (_isDisposed)
            throw new InvalidOperationException($"Script environment {Script.Id} is disposed.");
    }

    public void Dispose()
    {
        _logger.LogTrace($"{nameof(Dispose)} start");

        Dispose(true);
        GC.SuppressFinalize(this);

        _isDisposed = true;

        _logger.LogTrace($"{nameof(Dispose)} end");
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogTrace($"{nameof(DisposeAsync)} start");

        await DisposeAsyncCore().ConfigureAwait(false);

        Dispose(false);
        GC.SuppressFinalize(this);

        _isDisposed = true;

        _logger.LogTrace($"{nameof(DisposeAsync)} end");
    }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            AsyncUtil.RunSync(async () => await StopAsync());

            Script.RemoveAllPropertyChangedHandlers();
            Script.Config.RemoveAllPropertyChangedHandlers();

            _inputReader = ActionInputReader<string>.Null;
            _outputAdapter = ActionOutputWriter<object>.Null;

            _runtime?.Dispose();
            _runtime = null;

            _serviceScope.Dispose();
            _serviceScope = null!;
        }
    }

    protected async ValueTask DisposeAsyncCore()
    {
        await StopAsync();

        Script.RemoveAllPropertyChangedHandlers();
        Script.Config.RemoveAllPropertyChangedHandlers();

        _runtime?.Dispose();
        _runtime = null;

        if (_serviceScope is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _serviceScope.Dispose();
        }
    }
}
