using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Creates command execution scopes over application services without a dependency-injection package.</summary>
/// <example>
/// Application-owned instances:
/// <code>ScopeFactory = CommandScopes.FromServices(CommandServices.Empty.With&lt;IReportService&gt;(reports))</code>
/// A Microsoft.Extensions.DependencyInjection container, one service scope per invocation:
/// <code>ScopeFactory = CommandScopes.Create(provider.CreateAsyncScope, scope =&gt; scope.ServiceProvider)</code>
/// </example>
public static class CommandScopes
{
    /// <summary>
    /// Resolves every invocation's <c>[FromServices]</c> parameters from <paramref name="services"/>.
    /// The application owns those services: ending an invocation disposes nothing.
    /// </summary>
    public static ICommandExecutionScopeFactory FromServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new SharedScopeFactory(services);
    }

    /// <summary>
    /// Creates one scope per invocation with <paramref name="createScope"/> and resolves services through
    /// <paramref name="getServices"/>. When the invocation ends the scope is disposed, asynchronously when it
    /// implements <see cref="IAsyncDisposable"/>, otherwise through <see cref="IDisposable"/> when implemented.
    /// </summary>
    /// <typeparam name="TScope">The container's scope type, for example <c>AsyncServiceScope</c>.</typeparam>
    public static ICommandExecutionScopeFactory Create<TScope>(Func<TScope> createScope, Func<TScope, IServiceProvider> getServices)
        where TScope : notnull
    {
        ArgumentNullException.ThrowIfNull(createScope);
        ArgumentNullException.ThrowIfNull(getServices);
        return new OwnedScopeFactory<TScope>(createScope, getServices);
    }

    private sealed class SharedScopeFactory(IServiceProvider services) : ICommandExecutionScopeFactory
    {
        public ICommandExecutionScope CreateScope() => new SharedScope(services);
    }

    private sealed class SharedScope(IServiceProvider services) : ICommandExecutionScope
    {
        public IServiceProvider Services { get; } = services;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OwnedScopeFactory<TScope>(Func<TScope> createScope, Func<TScope, IServiceProvider> getServices) : ICommandExecutionScopeFactory
        where TScope : notnull
    {
        public ICommandExecutionScope CreateScope()
        {
            TScope scope = createScope() ?? throw new InvalidOperationException("The scope factory returned null.");
            IServiceProvider services;
            try
            {
                services = getServices(scope) ?? throw new InvalidOperationException("The scope returned no service provider.");
            }
            catch
            {
                if (scope is IDisposable disposable) disposable.Dispose();
                else if (scope is IAsyncDisposable asyncDisposable) asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }

            return new OwnedScope(scope, services);
        }
    }

    private sealed class OwnedScope(object scope, IServiceProvider services) : ICommandExecutionScope
    {
        public IServiceProvider Services { get; } = services;

        public ValueTask DisposeAsync()
        {
            if (scope is IAsyncDisposable asyncDisposable) return asyncDisposable.DisposeAsync();
            if (scope is IDisposable disposable) disposable.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>An immutable service provider over application-owned instances, for <see cref="CommandScopes.FromServices"/>.</summary>
/// <remarks>Lookup is by exact registered type. Every <see cref="With{TService}"/> call returns a new provider.</remarks>
public sealed class CommandServices : IServiceProvider
{
    private readonly Dictionary<Type, object> _services;

    private CommandServices(Dictionary<Type, object> services) => _services = services;

    /// <summary>Gets a provider that resolves nothing.</summary>
    public static CommandServices Empty { get; } = new(new Dictionary<Type, object>());

    /// <summary>Returns a provider that also resolves <typeparamref name="TService"/> to <paramref name="instance"/>, replacing an earlier registration of that type.</summary>
    public CommandServices With<TService>(TService instance) where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        var services = new Dictionary<Type, object>(_services) { [typeof(TService)] = instance };
        return new CommandServices(services);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return _services.TryGetValue(serviceType, out object? service) ? service : null;
    }
}
