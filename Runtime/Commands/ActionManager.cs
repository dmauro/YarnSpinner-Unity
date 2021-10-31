using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Yarn.Unity
{
    using Injector = Func<string, object>;
    using Converter = Func<string, object>;

    /// <summary>
    /// Create dispatchers for functions and commands.
    /// </summary>
    internal static class ActionManager
    {
        private const BindingFlags IgnoreVisiblity = BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AllInstanceMembers = BindingFlags.Instance | IgnoreVisiblity;
        private const BindingFlags AllStaticMembers = BindingFlags.Static | IgnoreVisiblity;
        private const BindingFlags AllMembers = AllInstanceMembers | AllStaticMembers;

        private static Injector GetDefaultMonoBehaviourInjector(Type behaviorType, string commandName)
        {
            if (!typeof(MonoBehaviour).IsAssignableFrom(behaviorType)) { return null; }
            return name =>
            {
                var gameObject = GameObject.Find(name);
                if (gameObject == null)
                {
                    Debug.LogError($"Can't run command {commandName} on {behaviorType.FullName} for {name}: " +
                        "an object with that name doesn't exist in the scene.");
                    return null;
                }

                var target = gameObject.GetComponent(behaviorType);
                if (target == null)
                {
                    Debug.LogError($"Can't run command {commandName} on {behaviorType.FullName} for {name}: " +
                        $"the command is only defined on {behaviorType.FullName} components, but {name} doesn't have one.");
                    return null;
                }
                return target;
            };
        }

        private static bool IsInjector(Type injectorType, MethodInfo injectorFunction, Type destinationType = null)
        {
            destinationType ??= injectorType;

            if (injectorFunction == null
                || !injectorFunction.IsStatic
                || injectorFunction.ReturnType == typeof(void)
                || !destinationType.IsAssignableFrom(injectorFunction.ReturnType))
            { return false; }

            var parameters = injectorFunction.GetParameters();
            return parameters.Count(param => !param.IsOptional) == 1
                && parameters[0].ParameterType == typeof(string);
        }

        private static Injector GetInjectorForMethod(Type injectorType, YarnCommandAttribute metadata)
        {
            var injectorFunction = metadata.Injector == null
                ? null
                : injectorType.GetMethod(metadata.Injector, AllStaticMembers);
            if (IsInjector(injectorType, injectorFunction))
            {
                return (Injector)injectorFunction.CreateDelegate(typeof(Injector));
            }
            return null;
        }

        private static Injector GetInjectorForType(Type injectorType, ref Dictionary<string, Injector> injectorCache)
        {
            string fullyQualifiedName = injectorType.AssemblyQualifiedName;
            if (!injectorCache.ContainsKey(fullyQualifiedName))
            {
                string injector = injectorType.GetCustomAttribute<YarnStateInjectorAttribute>()?.Injector;
                var injectorFunction = string.IsNullOrEmpty(injector) 
                    ? null 
                    : injectorType.GetMethod(injector, AllStaticMembers);
                if (IsInjector(injectorType, injectorFunction))
                {
                    injectorCache.Add(fullyQualifiedName, (Injector)injectorFunction.CreateDelegate(typeof(Injector)));
                }
                else
                {
                    // default cache to null so that next time, we know we've looked at least
                    injectorCache.Add(fullyQualifiedName, null);
                }
            }

            return injectorCache[fullyQualifiedName];
        }

        private static Converter[] CreateConverters(MethodInfo method)
        {
            return method.GetParameters().Select((param, i) => CreateConverter(method, param, i)).ToArray();
        }

        private static Converter CreateConverter(MethodInfo method, ParameterInfo parameter, int index)
        {
            var targetType = parameter.ParameterType;

            // well, I mean...
            if (targetType == typeof(string)) { return null; }

            // find the GameObject.
            if (typeof(GameObject).IsAssignableFrom(targetType))
            {
                return GameObject.Find;
            }

            // find components of the GameObject with the component, if available
            if (typeof(Component).IsAssignableFrom(targetType))
            {
                var paramMetadata = parameter.GetCustomAttribute<YarnParameterAttribute>();
                if (paramMetadata != null)
                {
                    var methodType = method.DeclaringType;
                    var injectorMeta = methodType.GetMethod(paramMetadata.Injector, AllStaticMembers);
                    if (IsInjector(methodType, injectorMeta, targetType))
                    {
                        return (Injector)injectorMeta.CreateDelegate(typeof(Injector));
                    }
                }

                return arg => GameObject.Find(arg)?.GetComponentInChildren(targetType);
            }

            // bools can take "true" or "false", or the parameter name.
            if (typeof(bool).IsAssignableFrom(targetType))
            {
                return arg =>
                {
                    if (arg.Equals(parameter.Name, StringComparison.InvariantCultureIgnoreCase)) { return true; }
                    if (bool.TryParse(arg, out bool res)) { return res; }
                    throw new ArgumentException(
                        $"Can't convert the given parameter at position {index + 1} (\"{arg}\") to parameter " +
                        $"{parameter.Name} of type {typeof(bool).FullName}.");
                };
            }

            // try converting using IConvertible.
            return arg =>
            {
                try
                {
                    return Convert.ChangeType(arg, targetType, CultureInfo.InvariantCulture);
                }
                catch (Exception e)
                {
                    throw new ArgumentException(
                        $"Can't convert the given parameter at position {index + 1} (\"{arg}\") to parameter " +
                        $"{parameter.Name} of type {targetType.FullName}: {e}");
                }
            };
        }

        private static DispatchCommand CreateCommandRunner(
            MethodInfo method, YarnCommandAttribute metadata, ref Dictionary<string, Injector> injectorCache)
        {
            var methodType = method.DeclaringType;
            if (methodType == null) { throw new ArgumentException($"Method {method.Name} does not have a type...somehow."); }

            Injector injector = null;
            if (!method.IsStatic)
            {
                injector = GetInjectorForMethod(methodType, metadata)
                    ?? GetInjectorForType(methodType, ref injectorCache)
                    ?? GetDefaultMonoBehaviourInjector(methodType, metadata.Name);
            }

            var converters = CreateConverters(method);

            return new DispatchCommand()
            {
                Method = method,
                Injector = injector,
                Converters = converters
            };
        }

        private static Type GetFuncType(int paramCount)
        {
            // this causes me physical pain
            return paramCount switch
            {
                0 => typeof(Func<>),
                1 => typeof(Func<,>),
                2 => typeof(Func<,,>),
                3 => typeof(Func<,,,>),
                4 => typeof(Func<,,,,>),
                5 => typeof(Func<,,,,,>),
                6 => typeof(Func<,,,,,,>),
                7 => typeof(Func<,,,,,,,>),
                8 => typeof(Func<,,,,,,,,>),
                9 => typeof(Func<,,,,,,,,,>),
                10 => typeof(Func<,,,,,,,,,,>),
                11 => typeof(Func<,,,,,,,,,,,>),
                12 => typeof(Func<,,,,,,,,,,,,>),
                13 => typeof(Func<,,,,,,,,,,,,,>),
                14 => typeof(Func<,,,,,,,,,,,,,,>),
                15 => typeof(Func<,,,,,,,,,,,,,,,>),
                16 => typeof(Func<,,,,,,,,,,,,,,,,>),
                _ =>  throw new ArgumentException("Delegates are limited to 16 parameters. Consider splitting up " +
                    "the implementation into multiple parts.")
            };
        }

        private static Delegate GetFunctionRunner(MethodInfo method)
        {
            var parameters = method.GetParameters();
            var paramTypes = parameters.Select(param => param.ParameterType).Append(method.ReturnType).ToArray();
            return method.CreateDelegate(GetFuncType(parameters.Length).MakeGenericType(paramTypes));
        }

        private static string GetActionName(YarnActionAttribute metadata, MethodInfo method)
        {
            return string.IsNullOrEmpty(metadata.Name) ? method.Name : metadata.Name;
        }

        private static void FindAllActions(
            out Dictionary<string, DispatchCommand> commands, out Dictionary<string, Delegate> functions)
        {
            commands = new Dictionary<string, DispatchCommand>();
            functions = new Dictionary<string, Delegate>();

            var injectorCache = new Dictionary<string, Injector>();

            // Find all assemblies
            var allMethods = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly => assembly.GetLoadableTypes())
                .SelectMany(type => type.GetMethods(AllMembers));

            foreach (var method in allMethods)
            {
                var command = method.GetCustomAttribute<YarnCommandAttribute>(false);
                if (command != null)
                {
                    var name = GetActionName(command, method);
                    try
                    {
                        commands.Add(name, CreateCommandRunner(method, command, ref injectorCache));
                    }
                    catch (ArgumentException)
                    {
                        MethodInfo existingDefinition = commands[name].Method;
                        Debug.LogError($"Can't add {method.DeclaringType.FullName}.{method.Name} for command {name} " +
                            $"because it's already defined on {existingDefinition.DeclaringType.FullName}.{existingDefinition.Name}");
                    }
                }

                var function = method.GetCustomAttribute<YarnFunctionAttribute>(false);
                if (function != null)
                {
                    var name = GetActionName(function, method);
                    try
                    {
                        functions.Add(name, GetFunctionRunner(method));
                    }
                    catch (ArgumentException e)
                    {
                        Debug.LogError($"Can't add {method.DeclaringType.FullName}.{method.Name} for command {name}: {e.Message}");
                    }
                }
            }
        }

        private static Dictionary<string, DispatchCommand> commands;
        private static Dictionary<string, Delegate> functions;

        /// <summary>
        /// Try to execute a command if it exists.
        /// </summary>
        /// <param name="name">Name of command.</param>
        /// <param name="args">Any arguments to pass in. Should be convertible
        /// based on the rules laid out in <see cref="YarnCommandAttribute"/>.
        /// </param>
        /// <param name="returnValue">The actual return value of the object.</param>
        /// <returns>Was a command executed?</returns>
        public static bool TryExecuteCommand(string[] args, out object returnValue)
        {
            if (!commands.TryGetValue(args[0], out var command))
            {
                returnValue = null;
                return false;
            }

            return command.TryInvoke(args, out returnValue);
        }

        /// <summary>
        /// Attempt to parse the arguments to apply to the method.
        /// </summary>
        /// <param name="method"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public static object[] ParseArgs(MethodInfo method, string[] args)
        {
            return ParseArgs(method, CreateConverters(method), args, true);
        }

        /// <summary>
        /// Attempt to parse the arguments with cached converters.
        /// </summary>
        /// <param name="method">The method to parse args for.</param>
        /// <param name="converters">Converters to use. Will be assumed that
        /// the converters correctly correspond to <paramref name="method"/>.
        /// </param>
        /// <param name="args">The raw list of arguments, including command and
        /// instance name.</param>
        /// <param name="isStatic">Should we treat this function as static?
        /// </param>
        /// <returns>The parsed arguments.</returns>
        public static object[] ParseArgs(
            MethodInfo method, Converter[] converters, string[] args, bool isStatic = false)
        {
            var parameters = method.GetParameters();
            int optional = parameters.Count(param => param.IsOptional);
            int required = parameters.Length - optional;
            int lead = isStatic ? 1 : 2;
            var count = args.Length - lead;

            if (optional > 0)
            {
                if (count < required || count > parameters.Length)
                {
                    throw new ArgumentException(
                        $"{method.Name} requires between {required} and {parameters.Length} parameters, but {count} " +
                        $"{(count == 1 ? "was" : "were")} provided.");
                }
            }
            else if (count != required)
            {
                throw new ArgumentException($"{method.Name} requires {required} parameters, but {count} " +
                    $"{(count == 1 ? "was" : "were")} provided.");
            }

            var finalArgs = new object[parameters.Length];

            for (int i = 0; i < count; i++)
            {
                string arg = args[i + lead];
                finalArgs[i] = converters[i] == null ? arg : converters[i].Invoke(arg);
            }
            for (int i = count; i < finalArgs.Length; i++)
            {
                finalArgs[i] = Type.Missing;
            }
            return finalArgs;
        }

        /// <summary>
        /// Register a function into a given library.
        /// </summary>
        /// <param name="library">Library instance to register.</param>
        public static void RegisterFunctions(Library library)
        {
            foreach (var (name, implementation) in functions)
            {
                library.RegisterFunction(name, implementation);
            }
        }
        
        static ActionManager()
        {
            FindAllActions(out commands, out functions);
        }
    }
}
