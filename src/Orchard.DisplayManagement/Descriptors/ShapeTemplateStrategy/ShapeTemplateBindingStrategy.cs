﻿using Microsoft.AspNet.Html.Abstractions;
using Microsoft.AspNet.Mvc;
using Microsoft.AspNet.Mvc.Infrastructure;
using Microsoft.AspNet.Mvc.Rendering;
using Microsoft.AspNet.Mvc.ViewEngines;
using Microsoft.AspNet.Mvc.ViewFeatures;
using Microsoft.AspNet.Mvc.ViewFeatures.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.OptionsModel;
using Orchard.DisplayManagement.Implementation;
using Orchard.Environment.Extensions;
using Orchard.Environment.Extensions.Models;
using Orchard.Environment.Extensions.Features;
using Orchard.Utility;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orchard.FileSystem.AppData;

namespace Orchard.DisplayManagement.Descriptors.ShapeTemplateStrategy
{
    public class ShapeTemplateBindingStrategy : IShapeTableProvider
    {
        private readonly IOrchardFileSystem _fileSystem;
        private readonly IEnumerable<IShapeTemplateHarvester> _harvesters;
        private readonly IEnumerable<IShapeTemplateViewEngine> _shapeTemplateViewEngines;
        private readonly IOptions<MvcViewOptions> _viewEngine;
        private readonly IActionContextAccessor _actionContextAccessor;
        private readonly ILogger _logger;
        private readonly IFeatureManager _featureManager;

        public ShapeTemplateBindingStrategy(
            IEnumerable<IShapeTemplateHarvester> harvesters,
            IFeatureManager featureManager,
            IOrchardFileSystem fileSystem,
            IEnumerable<IShapeTemplateViewEngine> shapeTemplateViewEngines,
            IOptions<MvcViewOptions> options,
            IActionContextAccessor actionContextAccessor,
            ILogger<DefaultShapeTableManager> logger)
        {
            _harvesters = harvesters;
            _featureManager = featureManager;
            _fileSystem = fileSystem;
            _shapeTemplateViewEngines = shapeTemplateViewEngines;
            _viewEngine = options;
            _actionContextAccessor = actionContextAccessor;
            _logger = logger;
        }

        public bool DisableMonitoring { get; set; }

        private static IEnumerable<ExtensionDescriptor> Once(IEnumerable<FeatureDescriptor> featureDescriptors)
        {
            var once = new ConcurrentDictionary<string, object>();
            return featureDescriptors.Select(fd => fd.Extension).Where(ed => once.TryAdd(ed.Id, null)).ToList();
        }

        public void Discover(ShapeTableBuilder builder)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Start discovering shapes");
            }
            var harvesterInfos = _harvesters.Select(harvester => new { harvester, subPaths = harvester.SubPaths() });

            var activeFeatures = _featureManager.GetEnabledFeaturesAsync().Result;
            var activeExtensions = Once(activeFeatures);

            var hits = activeExtensions.Select(extensionDescriptor =>
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Start discovering candidate views filenames");
                }
                var pathContexts = harvesterInfos.SelectMany(harvesterInfo => harvesterInfo.subPaths.Select(subPath =>
                {
                    var basePath = _fileSystem.Combine(extensionDescriptor.Location, extensionDescriptor.Id);
                    var virtualPath = _fileSystem.Combine(basePath, subPath);
                    IReadOnlyList<string> fileNames;

                    if (!_fileSystem.DirectoryExists(virtualPath))
                        fileNames = new List<string>();
                    else
                    {
                        fileNames = _fileSystem.ListFiles(virtualPath).Select(x => x.Name).ToReadOnlyCollection();
                    }

                    return new { harvesterInfo.harvester, basePath, subPath, virtualPath, fileNames };
                })).ToList();
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Done discovering candidate views filenames");
                }
                var fileContexts = pathContexts.SelectMany(pathContext => _shapeTemplateViewEngines.SelectMany(ve =>
                {
                    var fileNames = ve.DetectTemplateFileNames(pathContext.fileNames);
                    return fileNames.Select(
                        fileName => new
                        {
                            fileName = Path.GetFileNameWithoutExtension(fileName),
                            fileVirtualPath = _fileSystem.Combine(pathContext.virtualPath, fileName),
                            pathContext
                        });
                }));

                var shapeContexts = fileContexts.SelectMany(fileContext =>
                {
                    var harvestShapeInfo = new HarvestShapeInfo
                    {
                        SubPath = fileContext.pathContext.subPath,
                        FileName = fileContext.fileName,
                        TemplateVirtualPath = fileContext.fileVirtualPath
                    };
                    var harvestShapeHits = fileContext.pathContext.harvester.HarvestShape(harvestShapeInfo);
                    return harvestShapeHits.Select(harvestShapeHit => new { harvestShapeInfo, harvestShapeHit, fileContext });
                });

                return shapeContexts.Select(shapeContext => new { extensionDescriptor, shapeContext }).ToList();
            }).SelectMany(hits2 => hits2);


            foreach (var iter in hits)
            {
                // templates are always associated with the namesake feature of module or theme
                var hit = iter;
                var featureDescriptors = iter.extensionDescriptor.Features.Where(fd => fd.Id == hit.extensionDescriptor.Id);
                foreach (var featureDescriptor in featureDescriptors)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Binding {0} as shape [{1}] for feature {2}",
                        hit.shapeContext.harvestShapeInfo.TemplateVirtualPath,
                        iter.shapeContext.harvestShapeHit.ShapeType,
                        featureDescriptor.Id);
                    }
                    builder.Describe(iter.shapeContext.harvestShapeHit.ShapeType)
                        .From(new Feature { Descriptor = featureDescriptor })
                        .BoundAs(
                            hit.shapeContext.harvestShapeInfo.TemplateVirtualPath,
                            shapeDescriptor => displayContext => RenderAsync(shapeDescriptor, displayContext, hit.shapeContext.harvestShapeInfo, hit.shapeContext.harvestShapeHit));
                }
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Done discovering shapes");
            }
        }

        private async Task<IHtmlContent> RenderAsync(ShapeDescriptor shapeDescriptor, DisplayContext displayContext, HarvestShapeInfo harvestShapeInfo, HarvestShapeHit harvestShapeHit)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Rendering template file '{0}'", harvestShapeInfo.TemplateVirtualPath);
            }
            IHtmlContent result;

            if (displayContext.ViewContext.View != null)
            {
                var htmlHelper = MakeHtmlHelper(displayContext.ViewContext, displayContext.ViewContext.ViewData);
                result = htmlHelper.Partial(harvestShapeInfo.TemplateVirtualPath, displayContext.Value);
            }
            else
            {
                // If the View is null, it means that the shape is being executed from a non-view origin / where no ViewContext was established by the view engine, but manually.
                // Manually creating a ViewContext works when working with Shape methods, but not when the shape is implemented as a Razor view template.
                // Horrible, but it will have to do for now.
                result = await RenderRazorViewAsync(harvestShapeInfo.TemplateVirtualPath, displayContext);
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Done rendering template file '{0}'", harvestShapeInfo.TemplateVirtualPath);
            }
            return result;
        }

        private async Task<IHtmlContent> RenderRazorViewAsync(string path, DisplayContext context)
        {
            var viewEngineResult = _viewEngine.Value.ViewEngines.First().FindPartialView(_actionContextAccessor.ActionContext, path);
            if (viewEngineResult.Success)
            {
                using (var writer = new StringCollectionTextWriter(context.ViewContext.Writer.Encoding))
                {
                    // Forcing synchronous behavior so users don't have to await templates.
                    var view = viewEngineResult.View;
                    using (view as IDisposable)
                    {
                        var viewContext = new ViewContext(context.ViewContext, viewEngineResult.View, context.ViewContext.ViewData, writer);
                        await viewEngineResult.View.RenderAsync(viewContext);
                        return writer.Content;
                    }
                }
            }

            return null;
        }

        private static IHtmlHelper MakeHtmlHelper(ViewContext viewContext, ViewDataDictionary viewData)
        {
            var newHelper = viewContext.HttpContext.RequestServices.GetRequiredService<IHtmlHelper>();

            var contextable = newHelper as ICanHasViewContext;
            if (contextable != null)
            {
                var newViewContext = new ViewContext(viewContext, viewContext.View, viewData, viewContext.Writer);
                contextable.Contextualize(newViewContext);
            }

            return newHelper;
        }
    }
}