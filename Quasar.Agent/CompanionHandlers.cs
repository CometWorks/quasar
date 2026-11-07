using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Bridge;
using Magnetar.Protocol.Model;

namespace Quasar.Agent
{
    /// <summary>
    /// Finds a plugin's companion request handler: <see cref="IQuasarCompanionRequestHandler"/>, or for plugins
    /// compiled without Magnetar.Protocol (hub plugins) two public members found by name:
    /// <c>string QuasarCompanionPluginId { get; }</c> and
    /// <c>Task&lt;string&gt; HandleQuasarCompanionRequestAsync(string operation, string payloadJson, CancellationToken)</c>.
    /// The interface wins when a plugin has both.
    /// </summary>
    internal static class CompanionHandlers
    {
        private static readonly ConcurrentDictionary<Type, DuckMembers> Members =
            new ConcurrentDictionary<Type, DuckMembers>();

        /// <summary>The plugin's handler when it serves <paramref name="pluginId"/>; null otherwise.
        /// An empty declared plugin id falls back to <paramref name="loadedPluginId"/>.</summary>
        internal static IQuasarCompanionRequestHandler Find(
            object plugin,
            string loadedPluginId,
            string pluginId
        )
        {
            if (plugin == null)
                return null;

            IQuasarCompanionRequestHandler handler = plugin as IQuasarCompanionRequestHandler;
            string declared;
            if (handler != null)
            {
                declared = handler.PluginId;
            }
            else
            {
                var members = Members.GetOrAdd(plugin.GetType(), DuckMembers.Reflect);
                if (members == null)
                    return null;
                declared = members.PluginId(plugin);
                handler = new DuckHandler(plugin, members);
            }

            var id = string.IsNullOrWhiteSpace(declared) ? loadedPluginId : declared;
            return string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase) ? handler : null;
        }

        private sealed class DuckMembers
        {
            internal Func<object, string> PluginId;
            internal Func<object, string, string, CancellationToken, Task<string>> Handle;

            internal static DuckMembers Reflect(Type type)
            {
                var property = type.GetProperty(
                    "QuasarCompanionPluginId",
                    BindingFlags.Public | BindingFlags.Instance
                );
                var method = type.GetMethod(
                    "HandleQuasarCompanionRequestAsync",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string), typeof(string), typeof(CancellationToken) },
                    null
                );
                if (
                    property == null
                    || property.PropertyType != typeof(string)
                    || property.GetGetMethod() == null
                    || property.GetIndexParameters().Length != 0
                    || method == null
                    || method.ReturnType != typeof(Task<string>)
                )
                    return null;

                var instance = Expression.Parameter(typeof(object), "plugin");
                var typed = Expression.Convert(instance, type);
                var operation = Expression.Parameter(typeof(string), "operation");
                var payload = Expression.Parameter(typeof(string), "payloadJson");
                var token = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
                return new DuckMembers
                {
                    PluginId = Expression
                        .Lambda<Func<object, string>>(
                            Expression.Property(typed, property),
                            instance
                        )
                        .Compile(),
                    Handle = Expression
                        .Lambda<Func<object, string, string, CancellationToken, Task<string>>>(
                            Expression.Call(typed, method, operation, payload, token),
                            instance,
                            operation,
                            payload,
                            token
                        )
                        .Compile(),
                };
            }
        }

        private sealed class DuckHandler : IQuasarCompanionRequestHandler
        {
            private readonly object _plugin;
            private readonly DuckMembers _members;

            internal DuckHandler(object plugin, DuckMembers members)
            {
                _plugin = plugin;
                _members = members;
            }

            public string PluginId => _members.PluginId(_plugin);

            public async Task<CompanionPluginResponse> HandleQuasarCompanionRequestAsync(
                CompanionPluginRequest request,
                CancellationToken cancellationToken
            )
            {
                string payload;
                try
                {
                    payload = await _members
                        .Handle(_plugin, request.Operation, request.PayloadJson, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    return Failed(Unwrap(exception).Message);
                }

                return payload == null
                    ? Failed("Companion plugin returned no response.")
                    : new CompanionPluginResponse { Success = true, PayloadJson = payload };
            }

            // The agent fills the correlation id of every response.
            private static CompanionPluginResponse Failed(string error) =>
                new CompanionPluginResponse { Success = false, Error = error };

            private static Exception Unwrap(Exception exception)
            {
                while (
                    exception.InnerException != null
                    && (exception is TargetInvocationException || exception is AggregateException)
                )
                    exception = exception.InnerException;
                return exception;
            }
        }
    }
}
