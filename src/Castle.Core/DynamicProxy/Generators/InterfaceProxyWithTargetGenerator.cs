// Copyright 2004-2010 Castle Project - http://www.castleproject.org/
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Castle.DynamicProxy.Generators
{
	using System;
	using System.Collections.Generic;
#if !SILVERLIGHT
	using System.Reflection;
	using System.Xml.Serialization;
#endif
	using Castle.DynamicProxy.Contributors;
	using Castle.DynamicProxy.Generators.Emitters;
	using Castle.DynamicProxy.Generators.Emitters.SimpleAST;
	using Castle.DynamicProxy.Serialization;


	/// <summary>
	/// 
	/// </summary>
	public class InterfaceProxyWithTargetGenerator : BaseProxyGenerator
	{
		protected FieldReference targetField;


		public InterfaceProxyWithTargetGenerator(ModuleScope scope, Type @interface)
			: base(scope, @interface)
		{
			CheckNotGenericTypeDefinition(@interface, "@interface");
		}

		public Type GenerateCode(Type proxyTargetType, Type[] interfaces, ProxyGenerationOptions options)
		{
			// make sure ProxyGenerationOptions is initialized
			options.Initialize();

			CheckNotGenericTypeDefinition(proxyTargetType, "proxyTargetType");
			CheckNotGenericTypeDefinitions(interfaces, "interfaces");
			EnsureValidBaseType(options.BaseTypeForInterfaceProxy);
			Type proxyType;

			interfaces = TypeUtil.GetAllInterfaces(interfaces);
			CacheKey cacheKey = new CacheKey(proxyTargetType, targetType, interfaces, options);

			using (var locker = Scope.Lock.ForReadingUpgradeable())
			{
				Type cacheType = GetFromCache(cacheKey);
				if (cacheType != null)
				{
					Logger.Debug("Found cached proxy type {0} for target type {1}.", cacheType.FullName, targetType.FullName);
					return cacheType;
				}

				// Upgrade the lock to a write lock, then read again. This is to avoid generating duplicate types
				// under heavy multithreaded load.
				locker.Upgrade();

				cacheType = GetFromCache(cacheKey);
				if (cacheType != null)
				{
					Logger.Debug("Found cached proxy type {0} for target type {1}.", cacheType.FullName, targetType.FullName);
					return cacheType;
				}

				// Log details about the cache miss
				Logger.Debug("No cached proxy type was found for target type {0}.", targetType.FullName);
				EnsureOptionsOverrideEqualsAndGetHashCode(options);

				ProxyGenerationOptions = options;

				var name = Scope.NamingScope.GetUniqueName("Castle.Proxies." + targetType.Name + "Proxy");
				proxyType = GenerateType(name, proxyTargetType, interfaces, Scope.NamingScope.SafeSubScope());

				AddToCache(cacheKey, proxyType);
			}

			return proxyType;
		}

		private void EnsureValidBaseType(Type type)
		{
			if (type == null)
			{
				throw new ArgumentException(
					"Base type for proxy is null reference. Please set it to System.Object or some other valid type.");
			}

			if (!type.IsClass)
			{
				ThrowInvalidBaseType(type, "it is not a class type");
			}

			if(type.IsSealed)
			{
				ThrowInvalidBaseType(type, "it is sealed");
			}
#if !SILVERLIGHT
			var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			                                      null, Type.EmptyTypes, null);

			if (constructor == null || constructor.IsPrivate)
			{
				ThrowInvalidBaseType(type, "it does not have accessible parameterless constructor");
			}
#else
#warning this constructor exists in SL 3, so we can remove the if when we move to SL 3
#endif
		}

		private void ThrowInvalidBaseType(Type type, string doesNotHaveAccessibleParameterlessConstructor)
		{
			var format = "Type {0} is not valid base type for interface proxy, because {1}. Only a non-sealed class with non-private default constructor can be used as base type for interface proxy. Please use some other valid type.";
			throw new ArgumentException(string.Format(format, type, doesNotHaveAccessibleParameterlessConstructor));
		}

		protected virtual Type GenerateType(string typeName, Type proxyTargetType, Type[] interfaces, INamingScope namingScope)
		{
			IEnumerable<ITypeContributor> contributors;
			var allInterfaces = GetTypeImplementerMapping(interfaces, proxyTargetType, out contributors, namingScope);

			ClassEmitter emitter;
			FieldReference interceptorsField;
			Type baseType = Init(typeName, out emitter, proxyTargetType, out interceptorsField, allInterfaces);

			var model = new MetaType(typeName, ProxyGenerationOptions.BaseTypeForInterfaceProxy, allInterfaces);
			// Collect methods
			foreach (var contributor in contributors)
			{
				contributor.CollectElementsToProxy(ProxyGenerationOptions.Hook, model);
			}

			ProxyGenerationOptions.Hook.MethodsInspected();

			// Constructor

			var cctor = GenerateStaticConstructor(emitter);
			var ctorArguments = new List<FieldReference>();

			foreach (var contributor in contributors)
			{
				contributor.Generate(emitter, ProxyGenerationOptions);

				// TODO: redo it
				if (contributor is MixinContributor)
				{
					ctorArguments.AddRange((contributor as MixinContributor).Fields);
					
				}
			}

			ctorArguments.Add(interceptorsField);
			ctorArguments.Add(targetField);
			var selector = emitter.GetField("__selector");
			if (selector != null)
			{
				ctorArguments.Add(selector);
			}

			GenerateConstructors(emitter, baseType, ctorArguments.ToArray());

			// Complete type initializer code body
			CompleteInitCacheMethod(cctor.CodeBuilder);

			// Crosses fingers and build type
			Type generatedType = emitter.BuildType();

			InitializeStaticFields(generatedType);
			return generatedType;
		}

		protected virtual Type Init(string typeName, out ClassEmitter emitter, Type proxyTargetType, out FieldReference interceptorsField, IEnumerable<Type> interfaces)
		{
			Type baseType = ProxyGenerationOptions.BaseTypeForInterfaceProxy;

			emitter = BuildClassEmitter(typeName, baseType, interfaces);

			CreateFields(emitter, proxyTargetType);
			CreateTypeAttributes(emitter);

			interceptorsField = emitter.GetField("__interceptors");
			return baseType;
		}

		private void CreateFields(ClassEmitter emitter, Type proxyTargetType)
		{
			base.CreateFields(emitter);
			targetField = emitter.CreateField("__target", proxyTargetType);

#if SILVERLIGHT
#warning XmlIncludeAttribute is in silverlight, do we want to explore this?
#else
			emitter.DefineCustomAttributeFor<XmlIgnoreAttribute>(targetField);
#endif
		}

		protected override void CreateTypeAttributes(ClassEmitter emitter)
		{
			base.CreateTypeAttributes(emitter);
#if (!SILVERLIGHT)
			emitter.DefineCustomAttribute<SerializableAttribute>();
#endif
		}

		protected virtual string GeneratorType
		{
			get { return ProxyTypeConstants.InterfaceWithTarget; }
		}

		protected virtual bool AllowChangeTarget
		{
			get { return false; }
		}

		protected virtual IEnumerable<Type> GetTypeImplementerMapping(Type[] interfaces, Type proxyTargetType, out IEnumerable<ITypeContributor> contributors, INamingScope namingScope)
		{
			IDictionary<Type, ITypeContributor> typeImplementerMapping = new Dictionary<Type, ITypeContributor>();
			var mixins = new MixinContributor(namingScope, AllowChangeTarget) { Logger = Logger };
			// Order of interface precedence:
			// 1. first target
			ICollection<Type> targetInterfaces = TypeUtil.GetAllInterfaces(proxyTargetType);
			ICollection<Type> additionalInterfaces = TypeUtil.GetAllInterfaces(interfaces);
			var target = AddMappingForTargetType(typeImplementerMapping, proxyTargetType, targetInterfaces, additionalInterfaces,namingScope);

			// 2. then mixins
			if (ProxyGenerationOptions.HasMixins)
			{
				foreach (var mixinInterface in ProxyGenerationOptions.MixinData.MixinInterfaces)
				{
					if (targetInterfaces.Contains(mixinInterface))
					{
						// OK, so the target implements this interface. We now do one of two things:
						if(additionalInterfaces.Contains(mixinInterface))
						{
							// we intercept the interface, and forward calls to the target type
							AddMapping(mixinInterface, target, typeImplementerMapping);
						}
						// we do not intercept the interface
						mixins.AddEmptyInterface(mixinInterface);
					}
					else
					{
						if (!typeImplementerMapping.ContainsKey(mixinInterface))
						{
							mixins.AddInterfaceToProxy(mixinInterface);
							typeImplementerMapping.Add(mixinInterface, mixins);
						}
					}
				}
			}

			var additionalInterfacesContributor = GetContributorForAdditionalInterfaces(namingScope);
			// 3. then additional interfaces
			foreach (var @interface in additionalInterfaces)
			{
				if(typeImplementerMapping.ContainsKey(@interface)) continue;
				if(ProxyGenerationOptions.MixinData.ContainsMixin(@interface)) continue;

				additionalInterfacesContributor.AddInterfaceToProxy(@interface);
				AddMappingNoCheck(@interface, additionalInterfacesContributor, typeImplementerMapping);
			}

			// 4. plus special interfaces
			var instance = new InterfaceProxyInstanceContributor(targetType, GeneratorType, interfaces);
			AddMappingForISerializable(typeImplementerMapping, instance);
			try
			{
				AddMappingNoCheck(typeof(IProxyTargetAccessor), instance, typeImplementerMapping);
			}
			catch (ArgumentException)
			{
				HandleExplicitlyPassedProxyTargetAccessor(targetInterfaces, additionalInterfaces);
			}

			contributors = new List<ITypeContributor>
			{
				target,
				additionalInterfacesContributor,
				mixins,
				instance
			};
			return typeImplementerMapping.Keys;
		}

		protected virtual InterfaceProxyWithoutTargetContributor GetContributorForAdditionalInterfaces(INamingScope namingScope)
		{
			return new InterfaceProxyWithoutTargetContributor(namingScope, (c, m) => NullExpression.Instance) { Logger = Logger };
		}

		protected virtual ITypeContributor AddMappingForTargetType(IDictionary<Type, ITypeContributor> typeImplementerMapping, Type proxyTargetType, ICollection<Type> targetInterfaces, ICollection<Type> additionalInterfaces,INamingScope namingScope)
		{
			var contributor = new InterfaceProxyTargetContributor(proxyTargetType, AllowChangeTarget, namingScope)
			{ Logger = Logger };
			ICollection<Type> proxiedInterfaces = TypeUtil.GetAllInterfaces(targetType);
			foreach (var @interface in proxiedInterfaces)
			{
				contributor.AddInterfaceToProxy(@interface);
				AddMappingNoCheck(@interface, contributor, typeImplementerMapping);
			}

			foreach (var @interface in additionalInterfaces)
			{
				if (!ImplementedByTarget(targetInterfaces, @interface) || proxiedInterfaces.Contains(@interface))
				{
					continue;
				}

				contributor.AddInterfaceToProxy(@interface);
				AddMappingNoCheck(@interface, contributor, typeImplementerMapping);
			}
			return contributor;
		}

		private bool ImplementedByTarget(ICollection<Type> targetInterfaces, Type @interface)
		{
			return targetInterfaces.Contains(@interface);
		}
	}
}
