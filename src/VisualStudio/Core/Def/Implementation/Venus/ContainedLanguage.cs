﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Formatting.Rules;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.LanguageServices.Implementation.LanguageService;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.TextManager.Interop;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.Venus
{
    using Workspace = Microsoft.CodeAnalysis.Workspace;

    internal partial class ContainedLanguage<TPackage, TLanguageService>
        where TPackage : AbstractPackage<TPackage, TLanguageService>
        where TLanguageService : AbstractLanguageService<TPackage, TLanguageService>
    {
        private readonly IVsEditorAdaptersFactoryService _editorAdaptersFactoryService;
        private readonly IDiagnosticAnalyzerService _diagnosticAnalyzerService;
        private readonly TLanguageService _languageService;

        protected readonly VisualStudioWorkspace Workspace;
        protected readonly IComponentModel ComponentModel;

        public VisualStudioProject Project { get; }

        protected readonly ContainedDocument ContainedDocument;

        public IVsTextBufferCoordinator BufferCoordinator { get; protected set; }

        /// <summary>
        /// The subject (secondary) buffer that contains the C# or VB code.
        /// </summary>
        public ITextBuffer SubjectBuffer { get; }

        /// <summary>
        /// The underlying buffer that contains C# or VB code. NOTE: This is NOT the "document" buffer
        /// that is saved to disk.  Instead it is the view that the user sees.  The normal buffer graph
        /// in Venus includes 4 buffers:
        /// <code>
        ///            SurfaceBuffer/Databuffer (projection)
        ///             /                               |
        /// Subject Buffer (C#/VB projection)           |
        ///             |                               |
        /// Inert (generated) C#/VB Buffer         Document (aspx) buffer
        /// </code>
        /// In normal circumstance, the Subject and Inert C# buffer are identical in content, and the
        /// Surface and Document are also identical.  The Subject Buffer is the one that is part of the
        /// workspace, that most language operations deal with.  The surface buffer is the one that the
        /// view is created over, and the Document buffer is the one that is saved to disk.
        /// </summary>
        public ITextBuffer DataBuffer { get; }

        // Set when a TextViewFIlter is set.  We hold onto this to keep our TagSource objects alive even if Venus
        // disconnects the subject buffer from the view temporarily (which they do frequently).  Otherwise, we have to
        // re-compute all of the tag data when they re-connect it, and this causes issues like classification
        // flickering.
        private ITagAggregator<ITag> _bufferTagAggregator;

        [Obsolete("This is a compatibility shim for TypeScript; please do not use it.")]
        public ContainedLanguage(
            IVsTextBufferCoordinator bufferCoordinator,
            IComponentModel componentModel,
            AbstractProject project,
            IVsHierarchy hierarchy,
            uint itemid,
            TLanguageService languageService,
            SourceCodeKind sourceCodeKind,
            IFormattingRule vbHelperFormattingRule,
            Workspace workspace)
            : this(bufferCoordinator,
                   componentModel,
                   project.VisualStudioProject,
                   hierarchy,
                   itemid,
                   languageService,
                   vbHelperFormattingRule)
        {
        }

        public ContainedLanguage(
            IVsTextBufferCoordinator bufferCoordinator,
            IComponentModel componentModel,
            VisualStudioProject project,
            IVsHierarchy hierarchy,
            uint itemid,
            TLanguageService languageService,
            IFormattingRule vbHelperFormattingRule = null)
        {
            this.BufferCoordinator = bufferCoordinator;
            this.ComponentModel = componentModel;
            this.Project = project;
            _languageService = languageService;

            this.Workspace = componentModel.GetService<VisualStudioWorkspace>();

            _editorAdaptersFactoryService = componentModel.GetService<IVsEditorAdaptersFactoryService>();
            _diagnosticAnalyzerService = componentModel.GetService<IDiagnosticAnalyzerService>();

            // Get the ITextBuffer for the secondary buffer
            Marshal.ThrowExceptionForHR(bufferCoordinator.GetSecondaryBuffer(out var secondaryTextLines));
            var secondaryVsTextBuffer = (IVsTextBuffer)secondaryTextLines;
            SubjectBuffer = _editorAdaptersFactoryService.GetDocumentBuffer(secondaryVsTextBuffer);

            // Get the ITextBuffer for the primary buffer
            Marshal.ThrowExceptionForHR(bufferCoordinator.GetPrimaryBuffer(out var primaryTextLines));
            DataBuffer = _editorAdaptersFactoryService.GetDataBuffer((IVsTextBuffer)primaryTextLines);
            
            // Create our tagger
            var bufferTagAggregatorFactory = ComponentModel.GetService<IBufferTagAggregatorFactoryService>();
            _bufferTagAggregator = bufferTagAggregatorFactory.CreateTagAggregator<ITag>(SubjectBuffer);

            if (!ErrorHandler.Succeeded(((IVsProject)hierarchy).GetMkDocument(itemid, out var filePath)))
            {
                // we couldn't look up the document moniker from an hierarchy for an itemid.
                // Since we only use this moniker as a key, we could fall back to something else, like the document name.
                Debug.Assert(false, "Could not get the document moniker for an item from its hierarchy.");
                if (!hierarchy.TryGetItemName(itemid, out filePath))
                {
                    FatalError.Report(new System.Exception("Failed to get document moniker for a contained document"));
                }
            }

            var documentId = this.Project.AddSourceTextContainer(SubjectBuffer.AsTextContainer(), filePath);

            this.ContainedDocument = new ContainedDocument(
                componentModel.GetService<IThreadingContext>(),
                documentId,
                subjectBuffer: SubjectBuffer,
                dataBuffer: DataBuffer,
                bufferCoordinator,
                this.Workspace,
                project,
                hierarchy,
                itemid,
                componentModel,
                vbHelperFormattingRule);

            // TODO: Can contained documents be linked or shared?
            this.DataBuffer.Changed += OnDataBufferChanged;
        }

        private void OnDisconnect()
        {
            this.DataBuffer.Changed -= OnDataBufferChanged;
            this.Project.RemoveSourceTextContainer(SubjectBuffer.AsTextContainer());

            this.ContainedDocument.Dispose();
        }

        private void OnDataBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            // we don't actually care what has changed in primary buffer. we just want to re-analyze secondary buffer
            // when primary buffer has changed to update diagnostic positions.
            _diagnosticAnalyzerService.Reanalyze(this.Workspace, documentIds: SpecializedCollections.SingletonEnumerable(this.ContainedDocument.Id));
        }

        public void Dispose()
        {
            if (_bufferTagAggregator != null)
            {
                _bufferTagAggregator.Dispose();
                _bufferTagAggregator = null;
            }
        }
    }
}
