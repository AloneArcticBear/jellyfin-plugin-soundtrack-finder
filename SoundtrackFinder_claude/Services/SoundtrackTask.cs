// #claude
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SoundtrackFinder.Services;

/// <summary>
/// Nightly scheduled task: analyzes all titles that were not analyzed yet.
/// </summary>
public class SoundtrackTask : IScheduledTask
{
    private readonly AnalysisService _service;

    /// <summary>Initializes a new instance of the <see cref="SoundtrackTask"/> class.</summary>
    /// <param name="service">Analysis service.</param>
    public SoundtrackTask(AnalysisService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "Soundtrack Finder: Songs erkennen";

    /// <inheritdoc />
    public string Key => "SoundtrackFinderAnalyze";

    /// <inheritdoc />
    public string Description => "Erkennt per Shazam Songs in noch nicht analysierten Filmen und Folgen.";

    /// <inheritdoc />
    public string Category => "Soundtrack Finder";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.RunScheduledAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(5).Ticks
        };
    }
}

/// <summary>
/// Registers the plugin services.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient("SoundtrackFinder");
        serviceCollection.AddSingleton<ResultStore>();
        serviceCollection.AddSingleton<ShazamClient>();
        serviceCollection.AddSingleton<MusicExporter>();
        serviceCollection.AddSingleton<AnalysisService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<AnalysisService>());
        serviceCollection.AddSingleton<UserSettingsStore>();
        serviceCollection.AddSingleton<NowPlayingNotifier>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<NowPlayingNotifier>());
    }
}
