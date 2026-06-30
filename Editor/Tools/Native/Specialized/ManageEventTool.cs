using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;

namespace AgentCore.Editor.Tools.Native.Specialized
{
    /// <summary>
    /// Manage UnityEvent persistent listeners on GameObjects.
    /// Supports inspecting, adding, removing, and invoking UnityEvent callbacks.
    /// Uses UnityEditor.Events.UnityEventTools and reflection for persistent listener management.
    /// </summary>
    [AgentTool("manage_event",
        Description = "UnityEvent persistent listener wiring — connect UI callbacks and inter-object communication in the Editor. " +
                      "Actions: get_listeners (inspect all persistent callbacks on an event), add_listener (wire a method on target object), " +
                      "remove_listener (by index), list_events (find all UnityEvent fields on a component), " +
                      "set_call_state (EditorAndRuntime/RuntimeOnly/Off), get_count (number of listeners), invoke (fire event for testing). " +
                      "USE FOR: wiring Button.onClick to methods, connecting custom UnityEvents between components, " +
                      "inspecting what callbacks are registered on UI elements, testing event firing. " +
                      "NOT FOR: C# events/delegates (code-only, not serialized), creating new UnityEvent fields (that's script modification), " +
                      "event system input modules (use manage_ui). " +
                      "ACTIVATE WHEN: user mentions 'onClick', 'UnityEvent', 'button callback', 'event listener', 'wire up event', 'persistent listener'.",
        Category = "Specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageEventTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_listeners"", ""add_listener"", ""remove_listener"", ""clear_listeners"", ""list_events"", ""set_listener_state"", ""get_listener_count"", ""invoke""],
                    ""description"": ""Action to perform on UnityEvent listeners""
                },
                ""target"": { ""type"": ""string"", ""description"": ""Target GameObject name or path"" },
                ""component_type"": { ""type"": ""string"", ""description"": ""Component type name (e.g. Button, EventTrigger, MyScript)"" },
                ""event_name"": { ""type"": ""string"", ""description"": ""UnityEvent field/property name (e.g. onClick, onValueChanged, m_OnClick)"" },
                ""listener_object"": { ""type"": ""string"", ""description"": ""Listener target GameObject name or path"" },
                ""listener_component"": { ""type"": ""string"", ""description"": ""Listener target component type name (or 'GameObject' for GO methods)"" },
                ""method_name"": { ""type"": ""string"", ""description"": ""Method name to call on the listener target"" },
                ""index"": { ""type"": ""integer"", ""description"": ""Listener index (for remove/set_listener_state)"" },
                ""state"": {
                    ""type"": ""string"",
                    ""enum"": [""off"", ""runtime_only"", ""editor_and_runtime""],
                    ""description"": ""Listener call state""
                },
                ""arg_type"": {
                    ""type"": ""string"",
                    ""enum"": [""void"", ""int"", ""float"", ""string"", ""bool""],
                    ""description"": ""Argument type for add_listener (default: void)""
                },
                ""arg_value"": { ""description"": ""Argument value for add_listener (matches arg_type)"" },
                ""call_state"": {
                    ""type"": ""string"",
                    ""enum"": [""off"", ""runtime_only"", ""editor_and_runtime""],
                    ""description"": ""Call state for new listener (default: runtime_only)""
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for registration and LLM discovery.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_event",
            description: "Manage UnityEvent persistent listeners — get/add/remove listeners, list events on components, " +
                         "set listener call state, get listener count, and invoke events via reflection.",
            category: "Specialized",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Execute the requested event management action.
        /// </summary>
        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "get_listeners":
                        response = HandleGetListeners(parameters);
                        break;
                    case "add_listener":
                        response = HandleAddListener(parameters);
                        break;
                    case "remove_listener":
                        response = HandleRemoveListener(parameters);
                        break;
                    case "clear_listeners":
                        response = HandleClearListeners(parameters);
                        break;
                    case "list_events":
                        response = HandleListEvents(parameters);
                        break;
                    case "set_listener_state":
                        response = HandleSetListenerState(parameters);
                        break;
                    case "get_listener_count":
                        response = HandleGetListenerCount(parameters);
                        break;
                    case "invoke":
                        response = HandleInvoke(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_listeners, add_listener, remove_listener, clear_listeners, list_events, set_listener_state, get_listener_count, invoke");
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                response = ToolResponse.Fail(ex.Message);
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Unexpected error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        #region Action Handlers

        /// <summary>
        /// Get all persistent listeners of a UnityEvent.
        /// </summary>
        private ToolResponse HandleGetListeners(JObject parameters)
        {
            var (unityEvent, component, error) = FindUnityEvent(parameters);
            if (error != null) return error;

            int count = unityEvent.GetPersistentEventCount();
            var listeners = new List<object>();

            for (int i = 0; i < count; i++)
            {
                var target = unityEvent.GetPersistentTarget(i);
                var methodName = unityEvent.GetPersistentMethodName(i);
                var state = unityEvent.GetPersistentListenerState(i);

                listeners.Add(new
                {
                    index = i,
                    target = target != null ? target.name : "null",
                    targetType = target != null ? target.GetType().Name : "null",
                    method = methodName,
                    state = state.ToString()
                });
            }

            var eventName = ToolHelpers.GetRequiredString(parameters, "event_name");
            return ToolResponse.OkWithData(new
            {
                gameObject = component.gameObject.name,
                component = component.GetType().Name,
                eventName,
                listenerCount = count,
                listeners
            }, $"Found {count} persistent listeners on {component.GetType().Name}.{eventName}");
        }

        /// <summary>
        /// Add a persistent listener to a UnityEvent.
        /// Supports void, int, float, string, bool argument types.
        /// </summary>
        private ToolResponse HandleAddListener(JObject parameters)
        {
            var (unityEvent, component, error) = FindUnityEvent(parameters);
            if (error != null) return error;

            var listenerObjectName = ToolHelpers.GetRequiredString(parameters, "listener_object");
            var listenerComponentName = ToolHelpers.GetOptionalString(parameters, "listener_component", "GameObject");
            var methodName = ToolHelpers.GetRequiredString(parameters, "method_name");
            var argType = ToolHelpers.GetOptionalString(parameters, "arg_type", "void").ToLowerInvariant();
            var callStateStr = ToolHelpers.GetOptionalString(parameters, "call_state", "runtime_only");

            // Find listener target GameObject
            var listenerGo = ToolHelpers.FindGameObject(listenerObjectName);
            if (listenerGo == null)
                return ToolResponse.Fail($"Listener target GameObject not found: '{listenerObjectName}'");

            // Resolve target object and type
            UnityEngine.Object targetObj;
            Type targetType;

            if (listenerComponentName == "GameObject" || listenerComponentName == "UnityEngine.GameObject")
            {
                targetObj = listenerGo;
                targetType = typeof(GameObject);
            }
            else
            {
                var targetComponent = listenerGo.GetComponent(listenerComponentName);
                if (targetComponent == null)
                    return ToolResponse.Fail($"Listener component not found: '{listenerComponentName}' on '{listenerObjectName}'");
                targetObj = targetComponent;
                targetType = targetComponent.GetType();
            }

            // The event must be a standard UnityEvent for typed listeners
            var ue = unityEvent as UnityEvent;
            if (ue == null && argType != "void")
                return ToolResponse.Fail($"Field is not a standard UnityEvent. Generic UnityEvent<T> only supports void listeners via this tool.");

            Undo.RecordObject(component, "Add Event Listener");

            MethodInfo methodInfo;

            switch (argType)
            {
                case "void":
                    methodInfo = FindMethod(targetType, methodName, Type.EmptyTypes);
                    if (methodInfo == null)
                        return ToolResponse.Fail($"Method '{methodName}()' not found on {targetType.Name}");

                    if (ue != null)
                    {
                        var voidDelegate = Delegate.CreateDelegate(typeof(UnityAction), targetObj, methodInfo) as UnityAction;
                        UnityEventTools.AddPersistentListener(ue, voidDelegate);
                    }
                    else
                    {
                        // For UnityEventBase (generic events), use AddVoidPersistentListener
                        UnityEventTools.AddVoidPersistentListener(unityEvent,
                            Delegate.CreateDelegate(typeof(UnityAction), targetObj, methodInfo) as UnityAction);
                    }
                    break;

                case "float":
                    if (ue == null) return ToolResponse.Fail("Typed listeners require a standard UnityEvent");
                    methodInfo = FindMethod(targetType, methodName, new[] { typeof(float) });
                    if (methodInfo == null)
                        return ToolResponse.Fail($"Method '{methodName}(float)' not found on {targetType.Name}");
                    var floatVal = parameters["arg_value"]?.Value<float>() ?? 0f;
                    var floatDelegate = Delegate.CreateDelegate(typeof(UnityAction<float>), targetObj, methodInfo) as UnityAction<float>;
                    UnityEventTools.AddFloatPersistentListener(ue, floatDelegate, floatVal);
                    break;

                case "int":
                    if (ue == null) return ToolResponse.Fail("Typed listeners require a standard UnityEvent");
                    methodInfo = FindMethod(targetType, methodName, new[] { typeof(int) });
                    if (methodInfo == null)
                        return ToolResponse.Fail($"Method '{methodName}(int)' not found on {targetType.Name}");
                    var intVal = parameters["arg_value"]?.Value<int>() ?? 0;
                    var intDelegate = Delegate.CreateDelegate(typeof(UnityAction<int>), targetObj, methodInfo) as UnityAction<int>;
                    UnityEventTools.AddIntPersistentListener(ue, intDelegate, intVal);
                    break;

                case "string":
                    if (ue == null) return ToolResponse.Fail("Typed listeners require a standard UnityEvent");
                    methodInfo = FindMethod(targetType, methodName, new[] { typeof(string) });
                    if (methodInfo == null)
                        return ToolResponse.Fail($"Method '{methodName}(string)' not found on {targetType.Name}");
                    var strVal = parameters["arg_value"]?.ToString() ?? "";
                    var strDelegate = Delegate.CreateDelegate(typeof(UnityAction<string>), targetObj, methodInfo) as UnityAction<string>;
                    UnityEventTools.AddStringPersistentListener(ue, strDelegate, strVal);
                    break;

                case "bool":
                    if (ue == null) return ToolResponse.Fail("Typed listeners require a standard UnityEvent");
                    methodInfo = FindMethod(targetType, methodName, new[] { typeof(bool) });
                    if (methodInfo == null)
                        return ToolResponse.Fail($"Method '{methodName}(bool)' not found on {targetType.Name}");
                    var boolVal = parameters["arg_value"]?.Value<bool>() ?? false;
                    var boolDelegate = Delegate.CreateDelegate(typeof(UnityAction<bool>), targetObj, methodInfo) as UnityAction<bool>;
                    UnityEventTools.AddBoolPersistentListener(ue, boolDelegate, boolVal);
                    break;

                default:
                    return ToolResponse.Fail($"Unsupported arg_type: '{argType}'. Valid: void, int, float, string, bool");
            }

            // Set call state on the newly added listener
            int newIndex = unityEvent.GetPersistentEventCount() - 1;
            var callState = ParseCallState(callStateStr);
            unityEvent.SetPersistentListenerState(newIndex, callState);

            EditorUtility.SetDirty(component);

            var eventName = ToolHelpers.GetRequiredString(parameters, "event_name");
            return ToolResponse.OkWithData(new
            {
                message = $"Added listener {targetType.Name}.{methodName} to {component.GetType().Name}.{eventName}",
                index = newIndex,
                callState = callState.ToString()
            }, $"Added persistent listener at index {newIndex}");
        }

        /// <summary>
        /// Remove a persistent listener by index.
        /// </summary>
        private ToolResponse HandleRemoveListener(JObject parameters)
        {
            var (unityEvent, component, error) = FindUnityEvent(parameters);
            if (error != null) return error;

            var index = ToolHelpers.GetOptionalInt(parameters, "index", 0);
            int count = unityEvent.GetPersistentEventCount();

            if (index < 0 || index >= count)
                return ToolResponse.Fail($"Index {index} out of range. Event has {count} listeners (0-{count - 1}).");

            Undo.RecordObject(component, "Remove Event Listener");
            UnityEventTools.RemovePersistentListener(unityEvent, index);
            EditorUtility.SetDirty(component);

            return ToolResponse.OkWithData(new
            {
                removedIndex = index,
                remainingCount = unityEvent.GetPersistentEventCount()
            }, $"Removed listener at index {index}");
        }

        /// <summary>
        /// Clear all persistent listeners from a UnityEvent.
        /// </summary>
        private ToolResponse HandleClearListeners(JObject parameters)
        {
            var (unityEvent, component, error) = FindUnityEvent(parameters);
            if (error != null) return error;

            int count = unityEvent.GetPersistentEventCount();
            if (count == 0)
                return ToolResponse.Ok("Event has no listeners to clear.");

            Undo.RecordObject(component, "Clear Event Listeners");

            for (int i = count - 1; i >= 0; i--)
                UnityEventTools.RemovePersistentListener(unityEvent, i);

            EditorUtility.SetDirty(component);

            return ToolResponse.OkWithData(new
            {
                removed = count
            }, $"Cleared {count} listeners");
        }

        /// <summary>
        /// List all UnityEvent fields on a component.
        /// </summary>
        private ToolResponse HandleListEvents(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var componentTypeName = ToolHelpers.GetRequiredString(parameters, "component_type");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject not found: '{targetName}'");

            var component = go.GetComponent(componentTypeName);
            if (component == null)
                return ToolResponse.Fail($"Component '{componentTypeName}' not found on '{targetName}'");

            var type = component.GetType();
            var events = new List<object>();

            // Search fields
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                if (typeof(UnityEventBase).IsAssignableFrom(field.FieldType))
                {
                    var evt = field.GetValue(component) as UnityEventBase;
                    events.Add(new
                    {
                        name = field.Name,
                        type = field.FieldType.Name,
                        isPublic = field.IsPublic,
                        listenerCount = evt?.GetPersistentEventCount() ?? 0
                    });
                }
            }

            // Search properties
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var prop in properties)
            {
                if (typeof(UnityEventBase).IsAssignableFrom(prop.PropertyType) && prop.CanRead)
                {
                    try
                    {
                        var evt = prop.GetValue(component) as UnityEventBase;
                        events.Add(new
                        {
                            name = prop.Name,
                            type = prop.PropertyType.Name,
                            isPublic = prop.GetGetMethod() != null,
                            listenerCount = evt?.GetPersistentEventCount() ?? 0
                        });
                    }
                    catch
                    {
                        // Skip properties that throw on access
                    }
                }
            }

            return ToolResponse.OkWithData(new
            {
                gameObject = go.name,
                component = componentTypeName,
                count = events.Count,
                events
            }, $"Found {events.Count} UnityEvent fields on {componentTypeName}");
        }

        /// <summary>
        /// Set a listener's call state (Off, RuntimeOnly, EditorAndRuntime).
        /// </summary>
        private ToolResponse HandleSetListenerState(JObject parameters)
        {
            var (unityEvent, component, error) = FindUnityEvent(parameters);
            if (error != null) return error;

            var index = ToolHelpers.GetOptionalInt(parameters, "index", 0);
            var stateStr = ToolHelpers.GetRequiredString(parameters, "state");

            int count = unityEvent.GetPersistentEventCount();
            if (index < 0 || index >= count)
                return ToolResponse.Fail($"Index {index} out of range. Event has {count} listeners (0-{count - 1}).");

            var callState = ParseCallState(stateStr);

            Undo.RecordObject(component, "Set Listener State");
            unityEvent.SetPersistentListenerState(index, callState);
            EditorUtility.SetDirty(component);

            return ToolResponse.OkWithData(new
            {
                index,
                state = callState.ToString()
            }, $"Set listener {index} state to {callState}");
        }

        /// <summary>
        /// Get the persistent listener count of a UnityEvent.
        /// </summary>
        private ToolResponse HandleGetListenerCount(JObject parameters)
        {
            var (unityEvent, component, error) = FindUnityEvent(parameters);
            if (error != null) return error;

            var eventName = ToolHelpers.GetRequiredString(parameters, "event_name");
            int count = unityEvent.GetPersistentEventCount();

            return ToolResponse.OkWithData(new
            {
                gameObject = component.gameObject.name,
                component = component.GetType().Name,
                eventName,
                listenerCount = count
            }, $"{component.GetType().Name}.{eventName} has {count} persistent listeners");
        }

        /// <summary>
        /// Invoke a UnityEvent via reflection.
        /// </summary>
        private ToolResponse HandleInvoke(JObject parameters)
        {
            var (unityEvent, component, error) = FindUnityEvent(parameters);
            if (error != null) return error;

            var eventName = ToolHelpers.GetRequiredString(parameters, "event_name");

            // Find and call Invoke() via reflection
            var invokeMethod = unityEvent.GetType().GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public);
            if (invokeMethod == null)
                return ToolResponse.Fail($"Could not find Invoke method on {unityEvent.GetType().Name}. The event may require arguments.");

            // Only invoke parameterless Invoke()
            var invokeParams = invokeMethod.GetParameters();
            if (invokeParams.Length > 0)
                return ToolResponse.Fail($"Event '{eventName}' requires {invokeParams.Length} argument(s) ({string.Join(", ", invokeParams.Select(p => p.ParameterType.Name))}). " +
                                         "Only parameterless UnityEvent.Invoke() is supported.");

            try
            {
                invokeMethod.Invoke(unityEvent, null);
            }
            catch (TargetInvocationException ex)
            {
                return ToolResponse.Fail($"Event invoke failed: {(ex.InnerException ?? ex).Message}");
            }

            return ToolResponse.OkWithData(new
            {
                gameObject = component.gameObject.name,
                component = component.GetType().Name,
                eventName
            }, $"Invoked {component.GetType().Name}.{eventName}");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Find a UnityEventBase on a component by field/property name.
        /// Returns the event, the component, and an error response if any step fails.
        /// </summary>
        private (UnityEventBase unityEvent, Component component, ToolResponse error) FindUnityEvent(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var componentTypeName = ToolHelpers.GetRequiredString(parameters, "component_type");
            var eventName = ToolHelpers.GetRequiredString(parameters, "event_name");

            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return (null, null, ToolResponse.Fail($"GameObject not found: '{targetName}'"));

            var component = go.GetComponent(componentTypeName);
            if (component == null)
                return (null, null, ToolResponse.Fail($"Component '{componentTypeName}' not found on '{targetName}'"));

            var type = component.GetType();

            // Try field first, then property
            UnityEventBase unityEvent = null;

            var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
                unityEvent = field.GetValue(component) as UnityEventBase;

            if (unityEvent == null)
            {
                var property = type.GetProperty(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.CanRead)
                    unityEvent = property.GetValue(component) as UnityEventBase;
            }

            if (unityEvent == null)
                return (null, null, ToolResponse.Fail(
                    $"UnityEvent '{eventName}' not found on {componentTypeName}. Use 'list_events' action to see available events."));

            return (unityEvent, component, null);
        }

        /// <summary>
        /// Find a method on a type with specific parameter types.
        /// Also handles property setters (set_XXX pattern).
        /// </summary>
        private static MethodInfo FindMethod(Type targetType, string methodName, Type[] paramTypes)
        {
            var mi = targetType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, paramTypes, null);
            if (mi != null) return mi;

            // Handle property setter pattern: set_PropertyName
            if (methodName.StartsWith("set_") && paramTypes.Length == 1)
            {
                var propName = methodName.Substring(4);
                var prop = targetType.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
                if (prop != null && prop.CanWrite)
                {
                    var setter = prop.GetSetMethod();
                    if (setter != null && setter.GetParameters().Length == 1 &&
                        setter.GetParameters()[0].ParameterType == paramTypes[0])
                        return setter;
                }
            }

            return null;
        }

        /// <summary>
        /// Parse a call state string to UnityEventCallState enum.
        /// </summary>
        private static UnityEventCallState ParseCallState(string state)
        {
            if (string.IsNullOrEmpty(state))
                return UnityEventCallState.RuntimeOnly;

            switch (state.ToLowerInvariant().Replace(" ", "").Replace("_", ""))
            {
                case "off":
                    return UnityEventCallState.Off;
                case "runtimeonly":
                    return UnityEventCallState.RuntimeOnly;
                case "editorandruntime":
                    return UnityEventCallState.EditorAndRuntime;
                default:
                    return UnityEventCallState.RuntimeOnly;
            }
        }

        #endregion
    }
}
