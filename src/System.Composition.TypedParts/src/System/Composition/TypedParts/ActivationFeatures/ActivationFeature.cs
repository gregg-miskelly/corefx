﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Internal;
using System;
using System.Collections.Generic;
using System.Composition.Hosting.Core;
using System.Reflection;

namespace System.Composition.TypedParts.ActivationFeatures
{
    /// <summary>
    /// Allows modification of the activators generated for typed parts.
    /// </summary>
    internal abstract class ActivationFeature
    {
        /// <summary>
        /// A constant shared for subclass use.
        /// </summary>
        protected static readonly CompositionDependency[] NoDependencies = EmptyArray<CompositionDependency>.Value;

        /// <summary>
        /// Participate in the activator creation process.
        /// </summary>
        /// <param name="partType">The part type being activated.</param>
        /// <param name="activator">The activator body so far.</param>
        /// <param name="partMetadata">Metadata associated with the part being activated.</param>
        /// <param name="dependencies">Dependencies returned by a previous call to <see cref="GetDependencies"/>.</param>
        /// <returns>A new activator body, or the one already provided.</returns>
        public abstract CompositeActivator RewriteActivator(
            TypeInfo partType,
            CompositeActivator activator,
            IDictionary<string, object> partMetadata,
            IEnumerable<CompositionDependency> dependencies);

        /// <summary>
        /// Describe the dependencies required by this activation feature.
        /// </summary>
        /// <param name="partType">The part type being activated.</param>
        /// <param name="definitionAccessor">The definition accessor.</param>
        /// <returns>Dependencies.</returns>
        public virtual IEnumerable<CompositionDependency> GetDependencies(TypeInfo partType, DependencyAccessor definitionAccessor)
        {
            return NoDependencies;
        }
    }
}
