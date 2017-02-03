﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.IntegrationTesting;
using Microsoft.AspNetCore.Testing;
using Microsoft.AspNetCore.Testing.xunit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Sdk;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.Server.IISIntegration.FunctionalTests
{
    // Uses ports ranging 5050 - 5060.
    public class NtlmAuthenticationTests
    {
        private readonly ITestOutputHelper _output;

        public NtlmAuthenticationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [ConditionalTheory]
        [OSSkipCondition(OperatingSystems.Linux)]
        [OSSkipCondition(OperatingSystems.MacOSX)]
        //[InlineData(ServerType.IISExpress, RuntimeFlavor.CoreClr, RuntimeArchitecture.x86, "http://localhost:5050/", ApplicationType.Standalone)]
        [InlineData(ServerType.IISExpress, RuntimeFlavor.Clr, RuntimeArchitecture.x64, "http://localhost:5051/", ApplicationType.Portable)]
        // TODO reenable when https://github.com/dotnet/sdk/issues/696 is resolved
        //[InlineData(ServerType.IISExpress, RuntimeFlavor.CoreClr, RuntimeArchitecture.x64, "http://localhost:5052/", ApplicationType.Standalone)]
        public async Task NtlmAuthentication(ServerType serverType, RuntimeFlavor runtimeFlavor, RuntimeArchitecture architecture, string applicationBaseUrl, ApplicationType applicationType)
        {
            var factory = new LoggerFactory().AddDebug();
            factory.AddProvider(new XunitLoggerProvider(_output));
            var logger = factory.CreateLogger($"NtlmAuthentication:{serverType}:{runtimeFlavor}:{architecture}");

            using (logger.BeginScope("NtlmAuthenticationTest"))
            {
                var windowsRid = architecture == RuntimeArchitecture.x64
                    ? "win7-x64"
                    : "win7-x86";

                var deploymentParameters = new DeploymentParameters(Helpers.GetTestSitesPath(), serverType, runtimeFlavor, architecture)
                {
                    ApplicationBaseUriHint = applicationBaseUrl,
                    EnvironmentName = "NtlmAuthentication", // Will pick the Start class named 'StartupNtlmAuthentication'
                    ServerConfigTemplateContent = (serverType == ServerType.IISExpress) ? File.ReadAllText("NtlmAuthentation.config") : null,
                    SiteName = "NtlmAuthenticationTestSite", // This is configured in the NtlmAuthentication.config
                    TargetFramework = runtimeFlavor == RuntimeFlavor.Clr ? "net451" : "netcoreapp1.1",
                    ApplicationType = applicationType,
                    AdditionalPublishParameters = ApplicationType.Standalone == applicationType && RuntimeFlavor.CoreClr == runtimeFlavor
                        ? "-r " + windowsRid
                        : null
                };

                using (var deployer = ApplicationDeployerFactory.Create(deploymentParameters, logger))
                {
                    var deploymentResult = deployer.Deploy();
                    var httpClientHandler = new HttpClientHandler();
                    var httpClient = new HttpClient(httpClientHandler)
                    {
                        BaseAddress = new Uri(deploymentResult.ApplicationBaseUri),
                        Timeout = TimeSpan.FromSeconds(5),
                    };

                    // Request to base address and check if various parts of the body are rendered & measure the cold startup time.
                    var response = await RetryHelper.RetryRequest(() =>
                    {
                        return httpClient.GetAsync(string.Empty);
                    }, logger, deploymentResult.HostShutdownToken, retryCount: 30);

                    var responseText = await response.Content.ReadAsStringAsync();
                    try
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        Assert.Equal("Hello World", responseText);

                        response = await httpClient.GetAsync("/Anonymous");
                        responseText = await response.Content.ReadAsStringAsync();
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        Assert.Equal("Anonymous?True", responseText);

                        response = await httpClient.GetAsync("/Restricted");
                        responseText = await response.Content.ReadAsStringAsync();
                        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                        Assert.Contains("NTLM", response.Headers.WwwAuthenticate.ToString());
                        Assert.Contains("Negotiate", response.Headers.WwwAuthenticate.ToString());

                        response = await httpClient.GetAsync("/RestrictedNTLM");
                        responseText = await response.Content.ReadAsStringAsync();
                        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                        Assert.Contains("NTLM", response.Headers.WwwAuthenticate.ToString());
                        // Note we can't restrict a challenge to a specific auth type, the native auth modules always add themselves.
                        Assert.Contains("Negotiate", response.Headers.WwwAuthenticate.ToString());

                        response = await httpClient.GetAsync("/Forbidden");
                        responseText = await response.Content.ReadAsStringAsync();
                        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

                        httpClientHandler = new HttpClientHandler() { UseDefaultCredentials = true };
                        httpClient = new HttpClient(httpClientHandler) { BaseAddress = new Uri(deploymentResult.ApplicationBaseUri) };

                        response = await httpClient.GetAsync("/Anonymous");
                        responseText = await response.Content.ReadAsStringAsync();
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        Assert.Equal("Anonymous?True", responseText);

                        response = await httpClient.GetAsync("/AutoForbid");
                        responseText = await response.Content.ReadAsStringAsync();
                        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                    }
                    catch (XunitException)
                    {
                        logger.LogWarning(response.ToString());
                        logger.LogWarning(responseText);
                        throw;
                    }
                }
            }
        }
    }
}
