using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Soenneker.Blazor.Utils.ResourceLoader.Abstract;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

/// <summary>
/// No-op implementation of <see cref="IResourceLoader"/> for build-time rendering.
/// All Suite interops (Bar, Snackbar, Offcanvas, Check, Steps, etc.) use IResourceLoader to load CSS/JS;
/// in a headless build context that can hang. This completes immediately so every component renders.
/// </summary>
internal sealed class NoOpResourceLoader : IResourceLoader
{
    public ValueTask LoadStyle(string url, string? integrity = null, string? id = null, string? @class = null, string? media = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask LoadScript(string url, string? integrity = null, string? id = null, bool async = false, bool defer = false, bool module = false, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask LoadScriptAndWaitForVariable(string url, string variableName, string? integrity = null, string? id = null, bool async = false, bool defer = false, bool module = false, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask<IJSObjectReference?> ImportModule(string url, CancellationToken cancellationToken = default) => new ValueTask<IJSObjectReference?>((IJSObjectReference?)null);

    public ValueTask ImportModuleAndWait(string url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask ImportModuleAndWaitUntilAvailable(string url, string variableName, int timeoutMs = 5000, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask WaitForVariable(string variableName, int timeoutMs = 5000, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DisposeModule(string moduleId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
