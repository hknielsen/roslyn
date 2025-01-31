﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.Serialization;

namespace Microsoft.CodeAnalysis.DesignerAttribute
{
    /// <summary>
    /// Serialization typed used to pass information to/from OOP and VS.
    /// </summary>
    [DataContract]
    internal readonly struct DesignerAttributeData
    {
        /// <summary>
        /// The category specified in a <c>[DesignerCategory("...")]</c> attribute.
        /// </summary>
        [DataMember(Order = 0)]
        public readonly string? Category;

        /// <summary>
        /// The document this <see cref="Category"/> applies to.
        /// </summary>
        [DataMember(Order = 1)]
        public readonly DocumentId DocumentId;

        /// <summary>
        /// Path for this <see cref="DocumentId"/>.
        /// </summary>
        [DataMember(Order = 2)]
        public readonly string FilePath;

        public DesignerAttributeData(string? category, DocumentId documentId, string filePath)
        {
            Category = category;
            DocumentId = documentId;
            FilePath = filePath;
        }

        public DesignerAttributeData WithCategory(string? category)
            => new(category, DocumentId, FilePath);
    }
}
