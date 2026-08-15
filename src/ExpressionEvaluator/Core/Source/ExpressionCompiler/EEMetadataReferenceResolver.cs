// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Microsoft.CodeAnalysis.ExpressionEvaluator
{
    internal sealed class EEMetadataReferenceResolver : MetadataReferenceResolver
    {
        private readonly AssemblyIdentityComparer _identityComparer;
        private readonly IReadOnlyDictionary<string, ImmutableArray<(AssemblyIdentity Identity, MetadataReference Reference)>> _referencesBySimpleName;

#if DEBUG
        internal readonly Dictionary<AssemblyIdentity, (AssemblyIdentity? Identity, int Count)> Requests = [];
#endif

        internal EEMetadataReferenceResolver(
            AssemblyIdentityComparer identityComparer,
            IReadOnlyDictionary<string, ImmutableArray<(AssemblyIdentity Identity, MetadataReference Reference)>> referencesBySimpleName)
        {
            _identityComparer = identityComparer;
            _referencesBySimpleName = referencesBySimpleName;
        }

        public override bool ResolveMissingAssemblies => true;

        public override PortableExecutableReference? ResolveMissingAssembly(MetadataReference definition, AssemblyIdentity referenceIdentity)
        {
            (AssemblyIdentity? Identity, MetadataReference? Reference) result = default;
            if (_referencesBySimpleName.TryGetValue(referenceIdentity.Name, out var references))
            {
                result = GetBestMatch(definition, references, referenceIdentity);
            }
#if DEBUG
            if (!Requests.TryGetValue(referenceIdentity, out var request))
            {
                request = (referenceIdentity, 0);
            }
            Requests[referenceIdentity] = (result.Identity, request.Count + 1);
#endif
            return (PortableExecutableReference?)result.Reference;
        }

        public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string? baseFilePath, MetadataReferenceProperties properties)
            => throw ExceptionUtilities.Unreachable();

        public override bool Equals(object? other)
            => throw ExceptionUtilities.Unreachable();

        public override int GetHashCode()
            => throw ExceptionUtilities.Unreachable();

        private (AssemblyIdentity? Identity, MetadataReference? Reference) GetBestMatch(
            MetadataReference definition,
            ImmutableArray<(AssemblyIdentity Identity, MetadataReference Reference)> references,
            AssemblyIdentity referenceIdentity)
        {
            (AssemblyIdentity? Identity, MetadataReference? Reference) best = default;
            (AssemblyIdentity? Identity, MetadataReference? Reference) firstEquivalent = default;
            List<(AssemblyIdentity Identity, MetadataReference Reference)>? equivalents = null;

            foreach (var pair in references)
            {
                var identity = pair.Identity;
                var compareResult = _identityComparer.Compare(referenceIdentity, identity);
                switch (compareResult)
                {
                    case AssemblyIdentityComparer.ComparisonResult.NotEquivalent:
                        break;
                    case AssemblyIdentityComparer.ComparisonResult.Equivalent:
                        if (firstEquivalent.Reference is null)
                        {
                            firstEquivalent = pair;
                        }
                        else
                        {
                            if (equivalents is null)
                            {
                                equivalents = [((AssemblyIdentity)firstEquivalent.Identity!, firstEquivalent.Reference)];
                            }
                            equivalents.Add(pair);
                        }
                        break;
                    case AssemblyIdentityComparer.ComparisonResult.EquivalentIgnoringVersion:
                        if (best.Identity is null || identity.Version > best.Identity.Version)
                        {
                            best = pair;
                        }
                        break;
                    default:
                        throw ExceptionUtilities.UnexpectedValue(compareResult);
                }
            }

            if (equivalents is not null)
            {
                // Multiple assemblies with equivalent identities (e.g. the same assembly
                // loaded into parallel AssemblyLoadContexts, where one copy may be a stale
                // build with different content). Prefer the copy that actually defines the
                // top-level types the referencing module uses from this assembly; the
                // debugger's module enumeration order says nothing about which copy the
                // referencing module's load context bound.
                // See https://github.com/dotnet/roslyn/issues/55857
                var referencedTypes = GetReferencedTopLevelTypeNames(definition, referenceIdentity.Name);
                if (referencedTypes is not null)
                {
                    foreach (var pair in equivalents)
                    {
                        if (DefinesAllTypes(pair.Reference, referencedTypes))
                        {
                            return pair;
                        }
                    }
                }
            }

            if (firstEquivalent.Reference is not null)
            {
                return firstEquivalent;
            }

            return best;
        }

        /// <summary>
        /// Top-level type names (namespace, name) of TypeRefs in <paramref name="definition"/>
        /// whose resolution scope is an AssemblyRef with the given simple name. Nested TypeRefs
        /// are scoped to their outer TypeRef and resolve within the outer type, so only
        /// top-level names are needed. Returns null if the metadata cannot be read or no such
        /// TypeRefs exist.
        /// </summary>
        private static HashSet<(string Namespace, string Name)>? GetReferencedTopLevelTypeNames(MetadataReference definition, string assemblySimpleName)
        {
            HashSet<(string, string)>? names = null;
            foreach (var module in GetModules(definition))
            {
                var reader = module.MetadataReader;
                foreach (var handle in reader.TypeReferences)
                {
                    TypeReference typeRef;
                    try
                    {
                        typeRef = reader.GetTypeReference(handle);
                        if (typeRef.ResolutionScope.Kind != HandleKind.AssemblyReference)
                        {
                            continue;
                        }
                        var assemblyRef = reader.GetAssemblyReference((AssemblyReferenceHandle)typeRef.ResolutionScope);
                        if (!reader.StringComparer.Equals(assemblyRef.Name, assemblySimpleName, ignoreCase: true))
                        {
                            continue;
                        }
                        names ??= [];
                        names.Add((reader.GetString(typeRef.Namespace), reader.GetString(typeRef.Name)));
                    }
                    catch (BadImageFormatException)
                    {
                        return null;
                    }
                }
            }
            return names;
        }

        /// <summary>
        /// True if the assembly defines (as a top-level TypeDef) or forwards (as an
        /// ExportedType) every name in <paramref name="typeNames"/>.
        /// </summary>
        private static bool DefinesAllTypes(MetadataReference reference, HashSet<(string Namespace, string Name)> typeNames)
        {
            var remaining = new HashSet<(string, string)>(typeNames);
            foreach (var module in GetModules(reference))
            {
                var reader = module.MetadataReader;
                try
                {
                    foreach (var handle in reader.TypeDefinitions)
                    {
                        if (remaining.Count == 0)
                        {
                            return true;
                        }
                        var typeDef = reader.GetTypeDefinition(handle);
                        if (!typeDef.GetDeclaringType().IsNil)
                        {
                            continue;
                        }
                        remaining.Remove((reader.GetString(typeDef.Namespace), reader.GetString(typeDef.Name)));
                    }
                    foreach (var handle in reader.ExportedTypes)
                    {
                        if (remaining.Count == 0)
                        {
                            return true;
                        }
                        var exportedType = reader.GetExportedType(handle);
                        if (exportedType.Implementation.Kind == HandleKind.ExportedType)
                        {
                            // Nested exported type.
                            continue;
                        }
                        remaining.Remove((reader.GetString(exportedType.Namespace), reader.GetString(exportedType.Name)));
                    }
                }
                catch (BadImageFormatException)
                {
                    return false;
                }
            }
            return remaining.Count == 0;
        }

        private static ImmutableArray<ModuleMetadata> GetModules(MetadataReference reference)
        {
            if (reference is PortableExecutableReference peReference)
            {
                Metadata? metadata;
                try
                {
                    metadata = peReference.GetMetadataNoCopy();
                }
                catch (BadImageFormatException)
                {
                    return ImmutableArray<ModuleMetadata>.Empty;
                }
                switch (metadata)
                {
                    case AssemblyMetadata assemblyMetadata:
                        return assemblyMetadata.GetModules();
                    case ModuleMetadata moduleMetadata:
                        return ImmutableArray.Create(moduleMetadata);
                }
            }
            return ImmutableArray<ModuleMetadata>.Empty;
        }
    }
}
