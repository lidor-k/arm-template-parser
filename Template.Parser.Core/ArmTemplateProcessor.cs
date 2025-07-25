﻿using Azure.Deployments.Core.Configuration;
using Azure.Deployments.Core.Definitions.Schema;
using Azure.Deployments.Core.Resources;
using Azure.Deployments.Expression.Engines;
using Azure.Deployments.Templates.Engines;
using Azure.Deployments.Templates.Expressions;
using Azure.Deployments.Templates.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.ResourceStack.Common.Collections;
using Microsoft.WindowsAzure.ResourceStack.Common.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Template.Parser.Core
{
    /// <summary>
    /// Contains functionality to process all language expressions in ARM templates. 
    /// Generates placeholder values when parameter values are not provided.
    /// </summary>
    public class ArmTemplateProcessor
    {
        private readonly string armTemplate;
        private readonly string apiVersion;
        private readonly ILogger? logger;
        private Dictionary<string, List<string>> originalToExpandedMapping = new Dictionary<string, List<string>>();
        private Dictionary<string, string> expandedToOriginalMapping = new Dictionary<string, string>();
        private Dictionary<string, (TemplateResource resource, string expandedPath)> flattenedResources = new Dictionary<string, (TemplateResource, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Mapping between resources in the expanded template to their original resource in 
        /// the original template. Used to get line numbers.
        /// The key is the path in the expanded template with value being the path
        /// in the original template.
        /// </summary>
        public Dictionary<string, string> ResourceMappings = new Dictionary<string, string>();

        /// <summary>
        ///  Constructor for the ARM Template Processing library
        /// </summary>
        /// <param name="armTemplate">The ARM Template <c>JSON</c>. Must follow this schema: https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#</param>
        /// <param name="apiVersion">The deployment API version. Must be a valid version from the deploymetns list here: https://docs.microsoft.com/azure/templates/microsoft.resources/allversions</param>
        /// <param name="logger">A logger to report errors and debug information</param>
        public ArmTemplateProcessor(string armTemplate, string apiVersion = "2020-01-01", ILogger? logger = null)
        {
            this.armTemplate = armTemplate;
            this.apiVersion = apiVersion;
            this.logger = logger;
        }

        /// <summary>
        /// Processes the ARM template with placeholder parameters and deployment metadata.
        /// </summary>
        /// <returns>The processed template as a <c>JSON</c> object.</returns>
        public JToken ProcessTemplate()
        {
            return ProcessTemplate("", "");
        }

        /// <summary>
        /// Processes the ARM template with provided parameters and placeholder deployment metadata.
        /// </summary>
        /// <param name="parameters">The template parameters and their values <c>JSON</c>. Must follow this schema: https://schema.management.azure.com/schemas/2015-01-01/deploymentParameters.json#</param>
        /// <returns>The processed template as a <c>JSON</c> object.</returns>
        public JToken ProcessTemplate(string parameters)
        {
            return ProcessTemplate(parameters, "");
        }

        /// <summary>
        /// Processes the ARM template with provided parameters and deployment metadata.
        /// </summary>
        /// <param name="parameters">The template parameters and their values <c>JSON</c>. Must follow this schema: https://schema.management.azure.com/schemas/2015-01-01/deploymentParameters.json#</param>
        /// <param name="metadata">The deployment metadata <c>JSON</c>.</param>
        /// <returns>The processed template as a <c>JSON</c> object.</returns>
        public JToken ProcessTemplate(string parameters, string metadata)
        {
            InsensitiveDictionary<JToken> metadataDictionary = string.IsNullOrEmpty(metadata) ? PlaceholderInputGenerator.GeneratePlaceholderDeploymentMetadata() : PopulateDeploymentMetadata(metadata);
            return ProcessTemplate(parameters, metadataDictionary);
        }

        public JToken ProcessTemplate(string parameters, InsensitiveDictionary<JToken> metadataDictionary)
        {
            InsensitiveDictionary<JToken> definedParameters = new InsensitiveDictionary<JToken>();
            if (!string.IsNullOrEmpty(parameters))
            {
                definedParameters = PopulateParameters(parameters);
            }

            var generatedParameters = PopulateParameters(PlaceholderInputGenerator.GeneratePlaceholderParameters(armTemplate));

            RemoveUnusedParameters(definedParameters);

            definedParameters.AddRangeIfNotExists(generatedParameters);

            var template = ParseAndValidateTemplate(definedParameters, metadataDictionary);

            return template.ToJToken();
        }

        private void RemoveUnusedParameters(InsensitiveDictionary<JToken> definedParameters)
        {
            var jsonTemplate = JObject.Parse(armTemplate);

            var templateParameters = jsonTemplate.InsensitiveToken("parameters").Children<JProperty>().Select(o => o.Name).ToHashSet();

            foreach (var parameter in definedParameters.Keys)
            {
                var test = string.Empty;
                if (!templateParameters.TryGetValue(parameter, out test))
                {
                    definedParameters.Remove(parameter);
                }
            }
        }

        /// <summary>
        /// Parses and validates the template.
        /// </summary>
        /// <param name="parameters">The template parameters</param>
        /// <param name="metadata">The deployment metadata</param>
        /// <returns>The processed template as a Template object.</returns>
        internal Azure.Deployments.Core.Definitions.Schema.Template ParseAndValidateTemplate(InsensitiveDictionary<JToken> parameters, InsensitiveDictionary<JToken> metadata)
        {
            Dictionary<string, (string, int)> copyNameMap = new Dictionary<string, (string, int)>();

            Azure.Deployments.Core.Definitions.Schema.Template template = TemplateEngine.ParseTemplate(armTemplate);

            TemplateEngine.ValidateTemplate(template, apiVersion, TemplateDeploymentScope.NotSpecified);

            SetOriginalResourceNames(template);

            // If there are resources using copies, the original resource will
            // be removed from template.Resources and the copies will be added instead,
            // to the end of the array. This means that OriginalName will be lost
            // in the resource and will be out of order.
            // To work around this, build a map of copy name to OriginalName and index
            // so OriginalName can be updated and the order fixed after copies are finished
            for (int i = 0; i < template.Resources.Length; i++)
            {
                var resource = template.Resources[i];
                if (resource.Copy != null) copyNameMap[resource.Copy.Name.Value] = (resource.OriginalName, i);
            }

            var managementGroupName = metadata["managementGroup"]["name"]?.ToString();
            var subscriptionId = metadata["subscription"]["subscriptionId"]?.ToString();
            var resourceGroupName = metadata["resourceGroup"]["name"]?.ToString();

            try
            {
                TemplateEngine.ProcessTemplateLanguageExpressions(managementGroupName, subscriptionId, resourceGroupName, template, apiVersion, parameters, metadata, null);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("incorrect segment lengths"))
                {
                    // Processing stops when the error is found: some resources could be missing
                    // information that is needed for the remaining template processing,
                    // like updated values in their DependsOn and Name properties
                    throw;
                }

                // Do not throw if there was another issue with evaluating language expressions

                logger?.LogWarning(ex, "An exception occurred when processing the template language expressions");
            }

            MapTopLevelResources(template, copyNameMap);

            TemplateEngine.ValidateProcessedTemplate(template, apiVersion, TemplateDeploymentScope.NotSpecified);

            template = ProcessResourcesAndOutputs(template);

            return template;
        }

        /// <summary>
        /// Processes each resource for language expressions and parent resources as well
        /// as processes language expressions for outputs.
        /// </summary>
        /// <param name="template">Template being processed.</param>
        /// <returns>Template after processing resources and outputs.</returns>
        internal Azure.Deployments.Core.Definitions.Schema.Template ProcessResourcesAndOutputs(Azure.Deployments.Core.Definitions.Schema.Template template)
        {
            var evaluationHelper = GetTemplateFunctionEvaluationHelper(template);
            SaveFlattenedResources(template.Resources);

            foreach ((var resourceNameAndType, var resourceInfo) in flattenedResources)
            {
                ProcessTemplateResourceLanguageExpressions(resourceInfo.resource, evaluationHelper);

                if (!ResourceMappings.ContainsKey(resourceInfo.resource.Path))
                {
                    AddResourceMapping(resourceInfo.expandedPath, resourceInfo.resource.Path);
                }

                resourceInfo.resource.Type.Value = resourceNameAndType.Split(" ")[1];
            }

            if ((template.Outputs?.Count ?? 0) > 0 && template.Outputs != null)
            {
                // Recreate evaluation helper with newly parsed properties
                evaluationHelper = GetTemplateFunctionEvaluationHelper(template);

                foreach (var outputKey in template.Outputs.Keys.ToList())
                {
                    try
                    {
                        template.Outputs[outputKey].Value.Value = ExpressionsEngine.EvaluateLanguageExpressionsRecursive(
                            root: template.Outputs[outputKey].Value.Value,
                            evaluationContext: evaluationHelper.EvaluationContext);
                    }
                    catch (Exception)
                    {
                        logger?.LogWarning("The parsing of the template output named {outputName} failed", outputKey);
                        logger?.LogDebug("Output value: {outputValue}", template.Outputs[outputKey]?.Value?.Value?.ToString());

                        template.Outputs[outputKey].Value.Value = new JValue("NOT_PARSED");
                    }
                }
            }

            return template;
        }

        private void AddResourceMapping(string expandedTemplatePath, string originalTemplatePath)
        {
            // Save all permutations of the resource path based off values already present 
            // in the dictionary with mapping. This is necessary to report an issue in
            // a copied nth grandchild resource.
            var tokens = expandedTemplatePath.Split('.');
            for (int i = 0; i < tokens.Length - 1; i++)
            {
                string segmentOfExpandedPath = string.Join('.', tokens[..(i + 1)]);

                // Each segment of a path in the expanded template corresponds to one resource in the original template,
                // not necessarily the same index of resource, since copy loops reorder resources after processing.
                // And each resource in the original template could be copied to multiple locations in the expanded template:
                string? originalPathOfSegmentOfExpandedPath;
                if (expandedToOriginalMapping.TryGetValue(segmentOfExpandedPath, out originalPathOfSegmentOfExpandedPath))
                {
                    if (originalToExpandedMapping.TryGetValue(originalPathOfSegmentOfExpandedPath, out List<string>? copiedLocationsOfPathSegment))
                    {
                        foreach (string copiedLocationOfPathSegment in copiedLocationsOfPathSegment)
                        {
                            // This check is done to avoid assuming that the resource was copied to other top-level resources that don't necessarily depend on it:
                            if (copiedLocationOfPathSegment.Split('.').Length > 1)
                            {
                                var fullExpandedPath = $"{copiedLocationOfPathSegment}.{string.Join('.', tokens[(i + 1)..])}";
                                ResourceMappings.TryAdd(fullExpandedPath, originalTemplatePath);
                            }
                        }
                    }
                }
            }

            if (!ResourceMappings.TryAdd(expandedTemplatePath, originalTemplatePath) && ResourceMappings[expandedTemplatePath] != originalTemplatePath)
            {
                throw new Exception("Error processing resource dependencies: " +
                    $"{expandedTemplatePath} currently maps to {ResourceMappings[expandedTemplatePath]}, instead of {originalTemplatePath}.");
            }

            expandedToOriginalMapping[expandedTemplatePath] = originalTemplatePath;
            if (!originalToExpandedMapping.TryAdd(originalTemplatePath, new List<string> { expandedTemplatePath }))
            {
                originalToExpandedMapping[originalTemplatePath].Add(expandedTemplatePath);
            }
        }

        /// <summary>
        /// Flattens resources that are defined inside other resources.
        /// </summary>
        /// <param name="resources">Resources in the template.</param>
        /// <param name="parentName">Name of the parent resource. Used during recursive call.</param>
        /// <param name="parentType">Type of the parent resource. Used during recursive call.</param>
        /// <param name="parentExpandedPath">Path of the parent resource in the expanded template. Used during the recursive call.</param>
        private void SaveFlattenedResources(TemplateResource[] resources, string? parentName = null, string? parentType = null, string parentExpandedPath = "")
        {
            for (int i = 0; i < resources.Length; i++)
            {
                string dictionaryKey;
                var resource = resources[i];

                if (parentName != null && parentType != null)
                {
                    resource.Path = $"{flattenedResources[$"{parentName} {parentType}"].resource.Path}.resources[{i}]";

                    dictionaryKey = $"{parentName}/{resource.Name.Value} {parentType}/{resource.Type.Value}";
                }
                else
                {
                    if (resource.Path == "")
                    {
                        resource.Path = $"resources[{i}]";
                    }

                    dictionaryKey = $"{resource.Name.Value} {resource.Type.Value}";
                }

                var resourceExpandedPath = $"{(parentExpandedPath != "" ? parentExpandedPath + "." : "")}resources[{i}]";
                flattenedResources.Add(dictionaryKey, (resource, resourceExpandedPath));

                if (resource.Resources != null)
                {
                    string resourceNamePrefix = parentName == null ? "" : $"{parentName}/";
                    string resourceTypePrefix = parentType == null ? "" : $"{parentType}/";

                    SaveFlattenedResources(resource.Resources, $"{resourceNamePrefix}{resource.Name.Value}", $"{resourceTypePrefix}{resource.Type.Value}", resourceExpandedPath);
                }
            }
        }

        /// <summary>
        /// Processes language expressions in the properties property of the resources.
        /// </summary>
        /// <param name="templateResource">The template resource to process language expressions for.</param>
        /// <param name="evaluationHelper">Evaluation helper to evaluate expressions</param>
        private void ProcessTemplateResourceLanguageExpressions(TemplateResource templateResource, TemplateExpressionEvaluationHelper evaluationHelper)
        {
            try
            {
                if (templateResource.Properties != null)
                {
                    evaluationHelper.OnGetCopyContext = () => templateResource.CopyContext;
                    InsensitiveHashSet evaluationsToSkip = new InsensitiveHashSet();
                    if (templateResource.Type.Value.Equals("Microsoft.Resources/deployments", StringComparison.OrdinalIgnoreCase))
                    {
                        //evaluationsToSkip.Add("template");  // The tool should skip properties in nested templates to avoid false positive warnings
                    }

                    templateResource.Properties.Value = ExpressionsEngine.EvaluateLanguageExpressionsRecursive(
                        root: templateResource.Properties.Value,
                        evaluationContext: evaluationHelper.EvaluationContext,
                        skipEvaluationPaths: evaluationsToSkip);
                }
            }
            catch (Exception ex)
            {
                // Do not throw if there was an issue with evaluating language expressions

                // We are using the resource name instead of the resource path because nested templates have a relative path that could be ambiguous:
                logger?.LogWarning(ex, "An exception occurred while evaluating the properties of the resource named {resourceName}", templateResource.OriginalName);
                logger?.LogDebug("Properties: {properties}", templateResource.Properties.Value);

                return;
            }

            return;
        }

        /// <summary>
        /// Gets the template expression evaluation helper.
        /// </summary>
        /// <param name="template">The template.</param>
        /// <returns>The template expression evaluation helper</returns>
        private TemplateExpressionEvaluationHelper GetTemplateFunctionEvaluationHelper(Azure.Deployments.Core.Definitions.Schema.Template template)
        {
            var helper = new TemplateExpressionEvaluationHelper();

            var functionsLookup = template.GetFunctionDefinitions().ToInsensitiveDictionary(keySelector: function => function.Key, elementSelector: function => function.Function);

            var parametersLookup = template.Parameters.CoalesceEnumerable().ToInsensitiveDictionary(
                keySelector: parameter => parameter.Key,
                elementSelector: parameter => parameter.Value.Value);

            var variablesLookup = template.Variables.CoalesceEnumerable().ToInsensitiveDictionary(
                keySelector: variable => variable.Key,
                elementSelector: variable => variable.Value);
                          
            var schemaValidationContext = SchemaValidationContext.ForTemplate(template);

            helper.Initialize(
                metadata: template.Metadata,
                functionsLookup: functionsLookup,
                parametersLookup: parametersLookup,
                variablesLookup: variablesLookup,
                validationContext: schemaValidationContext,
                diagnostics: null);

            // Set reference lookup
            helper.OnGetTemplateReference = (TemplateReference templateReference) =>
            {
                foreach (var resource in template.Resources)
                {
                    if (new[] { resource.Name.Value, resource.OriginalName }.Contains(templateReference.ResourceId))
                    {
                        return resource.Properties?.Value;
                    }
                }
                return ExpressionsEngine.EvaluateLanguageExpressionsRecursive(
                    root: templateReference.ResourceId,
                    evaluationContext: helper.EvaluationContext);
            };

            return helper;
        }

        /// <summary>
        /// Maps the resources to their original location.
        /// </summary>
        /// <param name="template">The template</param>
        /// <param name="copyNameMap">Mapping of the copy name, the original name of the resource, and index of resource in resource list.</param>
        private void MapTopLevelResources(Azure.Deployments.Core.Definitions.Schema.Template template, Dictionary<string, (string, int)> copyNameMap)
        {
            // Set OriginalName back on resources that were copied
            // and map them to their original resource
            for (int i = 0; i < template.Resources.Length; i++)
            {
                var resource = template.Resources[i];
                if (resource.Copy != null && copyNameMap.TryGetValue(resource.Copy.Name.Value, out (string, int) originalValues))
                {
                    // Copied resource.  Update OriginalName and
                    // add mapping to original resource
                    resource.OriginalName = originalValues.Item1;
                    resource.Path = $"resources[{originalValues.Item2}]";

                    AddResourceMapping($"resources[{i}]", resource.Path);

                    continue;
                }

                AddResourceMapping($"resources[{i}]", resource.Path);
            }
        }

        /// <summary>
        /// Set the original name property for each resource before processing language expressions in the template.
        /// This is used to help map to the original resource after processing.
        /// </summary>
        /// <param name="template">The template</param>
        private void SetOriginalResourceNames(Azure.Deployments.Core.Definitions.Schema.Template template)
        {
            foreach (var resource in template.Resources)
            {
                resource.OriginalName = resource.Name.Value;
            }
        }

        /// <summary>
        /// Populates the deployment metadata data object.
        /// </summary>
        /// <param name="metadata">The deployment metadata <c>JSON</c>.</param>
        /// <returns>A dictionary with the metadata.</returns>
        internal InsensitiveDictionary<JToken> PopulateDeploymentMetadata(string metadata)
        {
            try
            {
                var metadataAsJObject = JObject.Parse(metadata);
                InsensitiveDictionary<JToken> metadataDictionary = new InsensitiveDictionary<JToken>();

                foreach (var property in metadataAsJObject.Properties())
                {
                    var value = property.Value.ToObject<JToken>();
                    if (value != null)
                    metadataDictionary.Add(property.Name, value);
                }

                return metadataDictionary;
            }
            catch (JsonReaderException ex)
            {
                throw new Exception($"Error parsing metadata: {ex}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error populating metadata: {ex}");
            }
        }

        /// <summary>
        /// Populates the parameters data object.
        /// </summary>
        /// <param name="parameters">The required input parameters and their values <c>JSON</c>.</param>
        /// <returns>A dictionary with required parameters.</returns>
        internal InsensitiveDictionary<JToken> PopulateParameters(string parameters)
        {
            // Create the minimum parameters needed
            JObject parametersObject = JObject.Parse(parameters);
            InsensitiveDictionary<JToken> parametersDictionary = new InsensitiveDictionary<JToken>();

            if (parametersObject["parameters"] == null)
            {
                throw new Exception("Parameters property is not specified in the ARM Template parameters provided. Please ensure ARM Template parameters follows the following JSON schema https://schema.management.azure.com/schemas/2015-01-01/deploymentParameters.json#");
            }

            foreach (var parameter in parametersObject.InsensitiveToken("parameters").Value<JObject>()?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                JToken? parameterValueAsJToken = parameter.Value.ToObject<JObject>()?.Property("value")?.Value;

                // See if "reference" was specified instead of "value"
                bool isReference = false;
                if (parameterValueAsJToken == null)
                {
                    parameterValueAsJToken = parameter.Value.ToObject<JObject>()?.Property("reference")?.Value;
                    if (parameterValueAsJToken != null) isReference = true;
                }

                parametersDictionary.Add(parameter.Name, isReference ? $"REF_NOT_AVAIL_{parameter.Name}" : parameterValueAsJToken ?? string.Empty);
            }

            return parametersDictionary;
        }
    }
}