﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Host.Bindings;
using Microsoft.Azure.WebJobs.Host.Protocols;
using Microsoft.Azure.WebJobs.Host.Triggers;
using Microsoft.Azure.WebJobs.Host.Indexers;
using System.Threading;

namespace Microsoft.Azure.WebJobs.Host.Config
{
    /// <summary>
    /// Helpers for adding binding rules to a given attribute.
    /// </summary>
    /// <typeparam name="TAttribute"></typeparam>
    public class FluentBindingRule<TAttribute> : FluentConverterRules<TAttribute, FluentBindingRule<TAttribute>>
        where TAttribute : Attribute
    {
        private readonly JobHostConfiguration _parent;

        private List<FluentBindToInputOptions> _binders = new List<FluentBindToInputOptions>();

        // Filters to apply to current binder
        private List<Func<TAttribute, bool>> _filters = new List<Func<TAttribute, bool>>();
        private StringBuilder _filterDescription = new StringBuilder();

        private Func<TAttribute, ParameterInfo, INameResolver, ParameterDescriptor> _hook;

        private Action<TAttribute, Type> _validator;

        internal FluentBindingRule(JobHostConfiguration parent)
        {
            _parent = parent;
        }

        internal override ConverterManager Converters
        {
            get
            {
                return (ConverterManager) _parent.ConverterManager;
            }
        }

        #region Filters

        private static PropertyInfo ResolveProperty(string propertyName)
        {
            var prop = typeof(TAttribute).GetProperty(propertyName);
            if (prop == null || !prop.CanRead)
            {
                throw new InvalidOperationException($"Attribute type {typeof(TAttribute).Name} does not have readable property '{propertyName}'");
            }
            return prop;
        }

        private void AppendFilter(string propertyName, string formatString)
        {
            if (_filterDescription.Length > 0)
            {
                _filterDescription.Append(" && ");
            }
            _filterDescription.AppendFormat(formatString, propertyName);
        }
         
        /// <summary>
        /// The subsequent Bind* operations only apply when the Attribute's property is null. 
        /// </summary>
        /// <param name="propertyName"></param>
        /// <returns></returns>
        public FluentBindingRule<TAttribute> WhenIsNull(string propertyName)
        {
            var prop = ResolveProperty(propertyName);
            Func<TAttribute, bool> func = (attribute) =>
            {
                var value = prop.GetValue(attribute);
                return value == null;
            };
            _filters.Add(func);
            AppendFilter(propertyName, "({0} == null)");

            return this;
        }

        /// <summary>
        /// The subsequent Bind* operations only apply when the Attribute's property is not null. 
        /// </summary>
        /// <param name="propertyName"></param>
        /// <returns></returns>
        public FluentBindingRule<TAttribute> WhenIsNotNull(string propertyName)
        {
            var prop = ResolveProperty(propertyName);
            Func<TAttribute, bool> func = (attribute) =>
            {
                var value = prop.GetValue(attribute);
                return value != null;
            };
            _filters.Add(func);
            AppendFilter(propertyName, "({0} != null)");

            return this;
        }

        /// <summary>
        /// The subsequent Bind* operations only apply when the Attribute's property is not null. 
        /// </summary>
        /// <param name="propertyName"></param>
        /// <returns></returns>
        public FluentBindingRule<TAttribute> When<TValue>(string propertyName, TValue expected)
        {
            var prop = ResolveProperty(propertyName);

            if (!typeof(TValue).IsEnum)
            {
                throw new InvalidOperationException($"Rule filter for '{propertyName}' can only be used with enums.");
            }

            var propType = prop.PropertyType;            
            if (!propType.IsAssignableFrom(typeof(TValue))) // Handles nullable
            {
                throw new InvalidOperationException(
                    $"Type mismatch. Property '{propertyName}' is type '{TypeUtility.GetFriendlyName(propType)}'. " + 
                    $"Can't compare to a value of type '{TypeUtility.GetFriendlyName(typeof(TValue))}");
            }

            Func<TAttribute, bool> func = (attribute) =>
            {
                object x = prop.GetValue(attribute);
                if (x is TValue val) // Handles nullable
                {
                    return val.Equals(expected);
                }
                return false;
            };
            _filters.Add(func);
            AppendFilter(propertyName, "({0} == '" + expected.ToString() + "')");

            return this;
        }

        internal FluentBindingRule<TAttribute> SetPostResolveHook(Func<TAttribute, ParameterInfo, INameResolver, ParameterDescriptor> hook)
        {
            _hook = hook;
            return this;
        }
        #endregion // Filters

        #region BindToInput
        /// <summary>
        /// Bind an attribute to the given input, using the converter manager. 
        /// </summary>
        /// <typeparam name="TType"></typeparam>
        /// <param name="builderInstance"></param>
        /// <returns></returns>
        public FluentBindToInputOptions BindToInput<TType>(IConverter<TAttribute, TType> builderInstance)
        {
            var pm = PatternMatcher.New(builderInstance);
            return BindToInput<TType>(pm);
        }

        /// <summary>
        /// Bind an attribute to the given input, using the converter manager. 
        /// </summary>
        /// <typeparam name="TType"></typeparam>
        /// <param name="builderInstance"></param>
        /// <returns></returns>
        public FluentBindToInputOptions BindToInput<TType>(IAsyncConverter<TAttribute, TType> builderInstance)
        {
            var pm = PatternMatcher.New(builderInstance);
            return BindToInput<TType>(pm);
        }

        /// <summary>
        /// General rule for binding to an generic input type for a given attribute. 
        /// </summary>
        /// <typeparam name="TType">The user type must be compatible with this type for the binding to apply.</typeparam>
        /// <param name="builderType">A that implements IConverter for the target parameter. 
        /// This will get instantiated with the appropriate generic args to perform the builder rule.</param>
        /// <param name="constructorArgs">constructor arguments to pass to the typeBuilder instantiation. This can be used 
        /// to flow state (like configuration, secrets, etc) from the configuration to the specific binding</param>
        /// <returns>A binding rule.</returns>
        public FluentBindToInputOptions BindToInput<TType>(
            Type builderType,
            params object[] constructorArgs)
        {
            var pm = PatternMatcher.New(builderType, constructorArgs);
            return BindToInput<TType>(pm);
        }

        /// <summary>
        /// Bind an attribute to the given input, using the supplied delegate to build the input from an resolved 
        /// instance of the attribute. 
        /// </summary>
        /// <typeparam name="TType"></typeparam>
        /// <param name="builder"></param>
        /// <returns></returns>
        public FluentBindToInputOptions BindToInput<TType>(Func<TAttribute, ValueBindingContext, Task<TType>> builder)        
        {
            var pm = PatternMatcher.New(builder);
            return BindToInput<TType>(pm);
        }

        /// <summary>
        /// Bind an attribute to the given input, using the supplied delegate to build the input from an resolved 
        /// instance of the attribute. 
        /// </summary>
        /// <typeparam name="TType"></typeparam>
        /// <param name="builder"></param>
        /// <returns></returns>
        public FluentBindToInputOptions BindToInput<TType>(Func<TAttribute, CancellationToken, Task<TType>> builder)
        {
            var pm = PatternMatcher.New(builder);
            return BindToInput<TType>(pm);
        }

        /// <summary>
        /// Bind an attribute to the given input, using the supplied delegate to build the input from an resolved 
        /// instance of the attribute. 
        /// </summary>
        /// <typeparam name="TType"></typeparam>
        /// <param name="builder"></param>
        /// <returns></returns>
        public FluentBindToInputOptions BindToInput<TType>(Func<TAttribute, TType> builder)
        {
            var pm = PatternMatcher.New(builder);
            return BindToInput<TType>(pm);
        }

        // Common worker for BindToInput rules. 
        private FluentBindToInputOptions BindToInput<TType>(PatternMatcher pm)
        {
            var rule = new BindToInputBindingProvider<TAttribute, TType>(_parent.NameResolver, _parent.ConverterManager, pm);
            return Bind(rule);
        }

        #endregion // BindToInput

        #region BindToStream

        /// <summary>
        /// Bind an attribute to a stream. This ensures the stream is flushed after the user function returns. 
        /// It uses the attribute's Access property to determine direction (Read/Write). 
        /// It includes rules for additional types of TextReader,string, byte[], and TextWriter,out string, out byte[].
        /// </summary>
        /// <param name="func"></param>
        /// <param name="fileAccess"></param>
        public void BindToStream(Func<TAttribute, ValueBindingContext, Task<Stream>> func, FileAccess fileAccess)
        {
            var nameResolver = _parent.NameResolver;
            var pm = PatternMatcher.New(func);
            var builder = pm.TryGetConverterFunc<TAttribute, Stream>(); // throws 
            var rule = new BindToStreamBindingProvider<TAttribute>(builder, fileAccess, nameResolver, _parent.ConverterManager);
            Bind(rule);
        }

        /// <summary>
        /// Bind an attribute to a stream. This ensures the stream is flushed after the user function returns. 
        /// It uses the attribute's Access property to determine direction (Read/Write). 
        /// It includes rules for additional types of TextReader,string, byte[], and TextWriter,out string, out byte[].
        /// </summary>
        /// <param name="builderInstance"></param>
        /// <param name="fileAccess"></param>
        public void BindToStream(IAsyncConverter<TAttribute, Stream> builderInstance, FileAccess fileAccess)
        {
            var pm = PatternMatcher.New(builderInstance);
            var builder = pm.TryGetConverterFunc<TAttribute, Stream>(); // throws 
            var nameResolver = _parent.NameResolver;
            var rule = new BindToStreamBindingProvider<TAttribute>(builder, fileAccess, nameResolver,  _parent.ConverterManager);
            Bind(rule);
        }

        /// <summary>
        /// Bind an attribute to a stream. This ensures the stream is flushed after the user function returns. 
        /// It uses the attribute's Access property to determine direction (Read/Write). 
        /// It includes rules for additional types of TextReader,string, byte[], and TextWriter,out string, out byte[].
        /// </summary>
        /// <param name="builderInstance"></param>
        /// <param name="fileAccess"></param>
        public void BindToStream(IConverter<TAttribute, Stream> builderInstance, FileAccess fileAccess)
        {
            var pm = PatternMatcher.New(builderInstance);
            var builder = pm.TryGetConverterFunc<TAttribute, Stream>(); // throws
            var nameResolver = _parent.NameResolver;
            var rule = new BindToStreamBindingProvider<TAttribute>(builder, fileAccess, nameResolver, _parent.ConverterManager);
            Bind(rule);
        }

        #endregion


        /// <summary>
        /// Rule to provide an IValueBinder from a resolved attribute. 
        /// IValueBinder will let you have an OnCompleted hook that is invoked after the user function completes. 
        /// </summary>
        /// <param name="builder">Builder function to create a IValueBinder given a resolved attribute and the user parameter type. </param>
        /// <returns>A binding provider that applies these semantics.</returns>
        public FluentBindToInputOptions BindToValueProvider(Func<TAttribute, Type, Task<IValueBinder>> builder)            
        {
            return BindToValueProvider<object>(builder);
        }

        /// <summary>
        /// Rule to provide an IValueBinder from a resolved attribute. 
        /// IValueBinder will let you have an OnCompleted hook that is invoked after the user function completes. 
        /// </summary>
        /// <typeparam name="TType">An Open Type. This rule is only applied if the parameter type matches the open type</typeparam>
        /// <param name="builder">Builder function to create a IValueBinder given a resolved attribute and the user parameter type. </param>
        /// <returns>A binding provider that applies these semantics.</returns>
        public FluentBindToInputOptions BindToValueProvider<TType>(Func<TAttribute, Type, Task<IValueBinder>> builder)
        {
            var ot = OpenType.FromType<TType>();
            var nameResolver = this._parent.NameResolver;
            var binder = new ItemBindingProvider<TAttribute>(nameResolver, builder, ot);
            return Bind(binder);            
        }


        /// <summary>
        /// Add a general binder.
        /// </summary>
        /// <param name="binder"></param>
        /// <returns></returns>
        public FluentBindToInputOptions Bind(IBindingProvider binder)
        {
            if (this._hook != null)
            {
                var fluidBinder = (FluentBindingProvider<TAttribute>)binder;
                fluidBinder.BuildParameterDescriptor = _hook;
                _hook = null;
            }

            // Apply filters
            if (this._filters.Count > 0)
            {
                var filters = this._filters.ToArray(); // produce copy 
                Func<TAttribute, Type, bool> predicate = (attribute, type) =>
                {                    
                    foreach (var filter in filters)
                    {
                        if (!filter(attribute))
                        {
                            return false;
                        }
                    }
                    return true;
                };
                binder = new FilteringBindingProvider<TAttribute>(
                    predicate, 
                    this._parent.NameResolver, 
                    binder, 
                    this._filterDescription.ToString());

                this._filterDescription.Clear();
                this._filters.Clear();    
            }

            var opts = new FluentBindToInputOptions(_parent, binder);
            _binders.Add(opts);
            return opts;
        }

        #region BindToCollector
        /// <summary>
        /// Bind to a collector 
        /// </summary>
        /// <typeparam name="TMessage"></typeparam>
        /// <param name="buildFromAttribute"></param>
        /// <returns></returns>
        public void BindToCollector<TMessage>(
            Func<TAttribute, IAsyncCollector<TMessage>> buildFromAttribute)
        {
            var pm = PatternMatcher.New(buildFromAttribute);
            BindToCollector<TMessage>(pm);
        }

        /// <summary>
        /// Bind to a collector. 
        /// </summary>
        /// <typeparam name="TMessage"></typeparam>
        /// <param name="buildFromAttribute"></param>
        /// <returns></returns>
        public void BindToCollector<TMessage>(
           IConverter<TAttribute, IAsyncCollector<TMessage>> buildFromAttribute)
        {
            var pm = PatternMatcher.New(buildFromAttribute);
            BindToCollector<TMessage>(pm);
        }

        /// <summary>
        /// Bind to a collector. 
        /// </summary>
        /// <typeparam name="TMessage"></typeparam>
        /// <param name="builderType"></param>
        /// <param name="constructorArgs"></param>
        /// <returns></returns>
        public void BindToCollector<TMessage>(
             Type builderType,
             params object[] constructorArgs)
        {
            var pm = PatternMatcher.New(builderType, constructorArgs);
            BindToCollector<TMessage>(pm);
        }

        private void BindToCollector<TMessage>(PatternMatcher pm)
        {
            var rule = new AsyncCollectorBindingProvider<TAttribute, TMessage>(_parent.NameResolver, _parent.ConverterManager, pm);
            Bind(rule);
        }

        #endregion // BindToCollector

        /// <summary>
        /// Setup a trigger binding for this attribute
        /// </summary>
        /// <param name="trigger"></param>
        public void BindToTrigger(ITriggerBindingProvider trigger)
        {
            if (_binders.Count > 0)
            {
                throw new InvalidOperationException($"The same attribute can't be bound to trigger and non-trigger bindings");
            }
            IExtensionRegistry extensions = _parent.GetService<IExtensionRegistry>();
            extensions.RegisterExtension<ITriggerBindingProvider>(trigger);
        }

        public void BindToTrigger<TTriggerValue>(ITriggerBindingProvider trigger = null)
                where TTriggerValue : class
        {
            if (trigger != null)
            {
                BindToTrigger(trigger);
            }

            var triggerBinder = new TriggerAdapterBindingProvider<TAttribute, TTriggerValue>(
                this._parent.NameResolver,
                this._parent.ConverterManager);
            Bind(triggerBinder);
        }

        /// <summary>
        /// Add a validator for the set of rules. 
        /// The validator will apply to all of these rules. 
        /// Prefer validation rules directly on the attribute or filter via When() rules.
        /// Use this only if you need to check against the  attribute and type together.
        /// </summary>
        /// <param name="validator"></param>
        /// <returns></returns>
        public void AddValidator(Action<TAttribute, Type> validator)
        {
            if (_validator != null)
            {
                throw new InvalidOperationException("Validator already set");
            }
            _validator = validator;
        }

        internal void DebugDumpGraph(TextWriter output)
        {
            var binding = CreateBinding() as IBindingRuleProvider;
            JobHostMetadataProvider.DumpRule(binding, output);
        }

        private IBindingProvider CreateBinding()
        {
            IBindingProvider[] bindings = _binders.Select(x => x.Binder).ToArray();
            var all = new GenericCompositeBindingProvider<TAttribute>(_validator, this._parent.NameResolver, bindings);
            return all;
        }

        // Called by infrastructure after the extension is invoked.
        // This applies all changes accumulated on the fluent object. 
        internal void ApplyRules()
        {
            if (_hook != null)
            {
                throw new InvalidOperationException("SetPostResolveHook() should be called before the Bind() it applies to.");
            }
            if (_filters.Count > 0)
            {
                throw new InvalidOperationException($"Filters ({_filterDescription}) should be called before the Bind() they apply to.");
            }

            if (_binders.Count > 0)
            {
                IExtensionRegistry extensions = _parent.GetService<IExtensionRegistry>();  
                var binding = CreateBinding();
                extensions.RegisterExtension<IBindingProvider>(binding);
                _binders.Clear();
            }
        }

        // Expose operations for customizing the current binder. 
        // These apply after the binder has matched.
        public class FluentBindToInputOptions
        {
            private readonly JobHostConfiguration _parent;

            internal FluentBindToInputOptions(JobHostConfiguration parent, IBindingProvider binder)
            {
                this._parent = parent;
                this.Binder = binder;
            }

            /// <summary>
            /// Add a validator for a specific rule. Invoked on this rule if the rule matches. 
            /// Validator is invoked once statically with a %% resolved attribute.
            /// </summary>
            /// <param name="validator"></param>
            public void AddValidator(Action<TAttribute, Type> validator)
            {
                var inner = this.Binder;
                var binder = new ValidatingWrapperBindingProvider<TAttribute>(validator, _parent.NameResolver, inner);
                this.Binder = binder;
            }

            internal IBindingProvider Binder;
        }
    }
}
