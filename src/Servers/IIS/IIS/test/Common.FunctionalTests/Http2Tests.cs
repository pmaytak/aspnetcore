// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http2Cat;
using Microsoft.AspNetCore.Server.IIS.FunctionalTests.Utilities;
using Microsoft.AspNetCore.Server.IntegrationTesting.Common;
using Microsoft.AspNetCore.Server.IntegrationTesting.IIS;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Microsoft.AspNetCore.Server.IIS.FunctionalTests
{
    [Collection(IISHttpsTestSiteCollection.Name)]
    public class Http2Tests
    {

        // TODO: Remove when the regression is fixed.
        // https://github.com/dotnet/aspnetcore/issues/23164#issuecomment-652646163
        private static readonly Version Win10_Regressed_DataFrame = new Version(10, 0, 20145, 0);
        private const string WindowsVersionForTrailers = "10.0.20300";

        public Http2Tests(IISTestSiteFixture fixture)
        {
            var port = TestPortHelper.GetNextSSLPort();
            fixture.DeploymentParameters.ApplicationBaseUriHint = $"https://localhost:{port}/";
            fixture.DeploymentParameters.AddHttpsToServerConfig();
            fixture.DeploymentParameters.SetWindowsAuth(false);
            Fixture = fixture;
        }

        public IISTestSiteFixture Fixture { get; }

        [QuarantinedTest("https://github.com/dotnet/aspnetcore/issues/26060")]
        [ConditionalTheory]
        [InlineData("GET")]
        [InlineData("HEAD")]
        [InlineData("PATCH")]
        [InlineData("DELETE")]
        [InlineData("CUSTOM")]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersions.Win10, SkipReason = "Http2 requires Win10")]
        public async Task Http2_MethodsRequestWithoutData_Success(string method)
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    var headers = new[]
                    {
                        new KeyValuePair<string, string>(HeaderNames.Method, method),
                        new KeyValuePair<string, string>(HeaderNames.Path, "/Http2_MethodsRequestWithoutData_Success"),
                        new KeyValuePair<string, string>(HeaderNames.Scheme, "https"),
                        new KeyValuePair<string, string>(HeaderNames.Authority, "localhost:443"),
                    };

                    await h2Connection.StartStreamAsync(1, headers, endStream: true);

                    await h2Connection.ReceiveHeadersAsync(1, decodedHeaders =>
                    {
                        Assert.Equal("200", decodedHeaders[HeaderNames.Status]);
                    });

                    var dataFrame = await h2Connection.ReceiveFrameAsync();
                    if (Environment.OSVersion.Version >= Win10_Regressed_DataFrame)
                    {
                        // TODO: Remove when the regression is fixed.
                        // https://github.com/dotnet/aspnetcore/issues/23164#issuecomment-652646163
                        Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: false, length: 0);

                        dataFrame = await h2Connection.ReceiveFrameAsync();
                    }
                    Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: true, length: 0);

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        [ConditionalTheory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersions.Win10, SkipReason = "Http2 requires Win10")]
        public async Task Http2_PostRequestWithoutData_LengthRequired(string method)
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    var headers = new[]
                    {
                        new KeyValuePair<string, string>(HeaderNames.Method, method),
                        new KeyValuePair<string, string>(HeaderNames.Path, "/"),
                        new KeyValuePair<string, string>(HeaderNames.Scheme, "https"),
                        new KeyValuePair<string, string>(HeaderNames.Authority, "localhost:443"),
                    };

                    await h2Connection.StartStreamAsync(1, headers, endStream: true);

                    await h2Connection.ReceiveHeadersAsync(1, decodedHeaders =>
                    {
                        Assert.Equal("411", decodedHeaders[HeaderNames.Status]);
                    });

                    var dataFrame = await h2Connection.ReceiveFrameAsync();
                    Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: false, length: 344);
                    dataFrame = await h2Connection.ReceiveFrameAsync();
                    Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: true, length: 0);

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        [ConditionalTheory]
        [InlineData("GET")]
        // [InlineData("HEAD")] Reset with code HTTP_1_1_REQUIRED
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("PATCH")]
        [InlineData("DELETE")]
        [InlineData("CUSTOM")]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersions.Win10_20H1, SkipReason = "Http2 requires Win10, and older versions of Win10 send some odd empty data frames.")]
        public async Task Http2_RequestWithDataAndContentLength_Success(string method)
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    var headers = new[]
                    {
                        new KeyValuePair<string, string>(HeaderNames.Method, method),
                        new KeyValuePair<string, string>(HeaderNames.Path, "/Http2_RequestWithDataAndContentLength_Success"),
                        new KeyValuePair<string, string>(HeaderNames.Scheme, "https"),
                        new KeyValuePair<string, string>(HeaderNames.Authority, "localhost:443"),
                        new KeyValuePair<string, string>(HeaderNames.ContentLength, "11"),
                    };

                    await h2Connection.StartStreamAsync(1, headers, endStream: false);

                    await h2Connection.SendDataAsync(1, Encoding.UTF8.GetBytes("Hello World"), endStream: true);

                    // Http.Sys no longer sends a window update here on later versions.
                    if (Environment.OSVersion.Version < new Version(10, 0, 19041, 0))
                    {
                        var windowUpdate = await h2Connection.ReceiveFrameAsync();
                        Assert.Equal(Http2FrameType.WINDOW_UPDATE, windowUpdate.Type);
                    }

                    await h2Connection.ReceiveHeadersAsync(1, decodedHeaders =>
                    {
                        Assert.Equal("200", decodedHeaders[HeaderNames.Status]);
                    });

                    var dataFrame = await h2Connection.ReceiveFrameAsync();
                    Assert.Equal(Http2FrameType.DATA, dataFrame.Type);
                    Assert.Equal(1, dataFrame.StreamId);

                    // Some versions send an empty data frame first.
                    if (dataFrame.PayloadLength == 0)
                    {
                        Assert.False(dataFrame.DataEndStream);
                        dataFrame = await h2Connection.ReceiveFrameAsync();
                        Assert.Equal(Http2FrameType.DATA, dataFrame.Type);
                        Assert.Equal(1, dataFrame.StreamId);
                    }

                    Assert.Equal(11, dataFrame.PayloadLength);
                    Assert.Equal("Hello World", Encoding.UTF8.GetString(dataFrame.Payload.Span));

                    if (!dataFrame.DataEndStream)
                    {
                        dataFrame = await h2Connection.ReceiveFrameAsync();
                        Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: true, length: 0);
                    }

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        [ConditionalTheory]
        [InlineData("GET")]
        // [InlineData("HEAD")] Reset with code HTTP_1_1_REQUIRED
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("PATCH")]
        [InlineData("DELETE")]
        [InlineData("CUSTOM")]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersions.Win10_20H1, SkipReason = "Http2 requires Win10, and older versions of Win10 send some odd empty data frames.")]
        public async Task Http2_RequestWithDataAndNoContentLength_Success(string method)
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    var headers = new[]
                    {
                        new KeyValuePair<string, string>(HeaderNames.Method, method),
                        new KeyValuePair<string, string>(HeaderNames.Path, "/Http2_RequestWithDataAndNoContentLength_Success"),
                        new KeyValuePair<string, string>(HeaderNames.Scheme, "https"),
                        new KeyValuePair<string, string>(HeaderNames.Authority, "localhost:443"),
                    };

                    await h2Connection.StartStreamAsync(1, headers, endStream: false);

                    await h2Connection.SendDataAsync(1, Encoding.UTF8.GetBytes("Hello World"), endStream: true);

                    // Http.Sys no longer sends a window update here on later versions.
                    if (Environment.OSVersion.Version < new Version(10, 0, 19041, 0))
                    {
                        var windowUpdate = await h2Connection.ReceiveFrameAsync();
                        Assert.Equal(Http2FrameType.WINDOW_UPDATE, windowUpdate.Type);
                    }

                    await h2Connection.ReceiveHeadersAsync(1, decodedHeaders =>
                    {
                        Assert.Equal("200", decodedHeaders[HeaderNames.Status]);
                    });

                    var dataFrame = await h2Connection.ReceiveFrameAsync();
                    Assert.Equal(Http2FrameType.DATA, dataFrame.Type);
                    Assert.Equal(1, dataFrame.StreamId);

                    // Some versions send an empty data frame first.
                    if (dataFrame.PayloadLength == 0)
                    {
                        Assert.False(dataFrame.DataEndStream);
                        dataFrame = await h2Connection.ReceiveFrameAsync();
                        Assert.Equal(Http2FrameType.DATA, dataFrame.Type);
                        Assert.Equal(1, dataFrame.StreamId);
                    }

                    Assert.Equal(11, dataFrame.PayloadLength);
                    Assert.Equal("Hello World", Encoding.UTF8.GetString(dataFrame.Payload.Span));

                    if (!dataFrame.DataEndStream)
                    {
                        dataFrame = await h2Connection.ReceiveFrameAsync();
                        Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: true, length: 0);
                    }

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersions.Win10_20H1, SkipReason = "Http2 requires Win10, and older versions of Win10 send some odd empty data frames.")]
        public async Task Http2_ResponseWithData_Success()
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    await h2Connection.StartStreamAsync(1, GetHeaders("/Http2_ResponseWithData_Success"), endStream: true);

                    await h2Connection.ReceiveHeadersAsync(1, decodedHeaders =>
                    {
                        Assert.Equal("200", decodedHeaders[HeaderNames.Status]);
                    });

                    var dataFrame = await h2Connection.ReceiveFrameAsync();
                    Assert.Equal(Http2FrameType.DATA, dataFrame.Type);
                    Assert.Equal(1, dataFrame.StreamId);

                    // Some versions send an empty data frame first.
                    if (dataFrame.PayloadLength == 0)
                    {
                        Assert.False(dataFrame.DataEndStream);
                        dataFrame = await h2Connection.ReceiveFrameAsync();
                        Assert.Equal(Http2FrameType.DATA, dataFrame.Type);
                        Assert.Equal(1, dataFrame.StreamId);
                    }

                    Assert.Equal(11, dataFrame.PayloadLength);
                    Assert.Equal("Hello World", Encoding.UTF8.GetString(dataFrame.Payload.Span));

                    if (!dataFrame.DataEndStream)
                    {
                        dataFrame = await h2Connection.ReceiveFrameAsync();
                        Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: true, length: 0);
                    }

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }


        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers)]
        public async Task ResponseTrailers_HTTP2_TrailersAvailable()
        {
            var response = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_HTTP2_TrailersAvailable");

            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Empty(response.TrailingHeaders);
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers)]
        public async Task ResponseTrailers_HTTP1_TrailersNotAvailable()
        {
            var response = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_HTTP1_TrailersNotAvailable");

            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpVersion.Version11, response.Version);
            Assert.Empty(response.TrailingHeaders);
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers)]
        public async Task ResponseTrailers_ProhibitedTrailers_Blocked()
        {
            var response = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_ProhibitedTrailers_Blocked");

            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Empty(response.TrailingHeaders);
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers)]
        public async Task ResponseTrailers_NoBody_TrailersSent()
        {
            var response = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_NoBody_TrailersSent");

            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.NotEmpty(response.TrailingHeaders);
            Assert.Equal("TrailerValue", response.TrailingHeaders.GetValues("TrailerName").Single());
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers)]
        public async Task ResponseTrailers_WithBody_TrailersSent()
        {
            var response = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_WithBody_TrailersSent");

            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Equal("Hello World", await response.Content.ReadAsStringAsync());
            Assert.NotEmpty(response.TrailingHeaders);
            Assert.Equal("Trailer Value", response.TrailingHeaders.GetValues("TrailerName").Single());
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers)]
        public async Task ResponseTrailers_WithContentLengthBody_TrailersSent()
        {
            var body = "Hello World";

            var response = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_WithContentLengthBody_TrailersSent");

            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
            Assert.NotEmpty(response.TrailingHeaders);
            Assert.Equal("Trailer Value", response.TrailingHeaders.GetValues("TrailerName").Single());
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers)]
        public async Task ResponseTrailers_WithTrailersBeforeContentLengthBody_TrailersSent()
        {
            var body = "Hello World";

            var response = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_WithTrailersBeforeContentLengthBody_TrailersSent");

            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpVersion.Version20, response.Version);
            // Avoid HttpContent's automatic content-length calculation.
            Assert.True(response.Content.Headers.TryGetValues(HeaderNames.ContentLength, out var contentLength), HeaderNames.ContentLength);
            Assert.Equal((2 * body.Length).ToString(CultureInfo.InvariantCulture), contentLength.First());
            Assert.Equal(body + body, await response.Content.ReadAsStringAsync());
            Assert.NotEmpty(response.TrailingHeaders);
            Assert.Equal("Trailer Value", response.TrailingHeaders.GetValues("TrailerName").Single());
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers)]
        public async Task ResponseTrailers_WithContentLengthBodyAndDeclared_TrailersSent()
        {
            var body = "Hello World";

            var response = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_WithContentLengthBodyAndDeclared_TrailersSent");

            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpVersion.Version20, response.Version);
            // Avoid HttpContent's automatic content-length calculation.
            Assert.True(response.Content.Headers.TryGetValues(HeaderNames.ContentLength, out var contentLength), HeaderNames.ContentLength);
            Assert.Equal(body.Length.ToString(CultureInfo.InvariantCulture), contentLength.First());
            Assert.Equal("TrailerName", response.Headers.Trailer.Single());
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
            Assert.NotEmpty(response.TrailingHeaders);
            Assert.Equal("Trailer Value", response.TrailingHeaders.GetValues("TrailerName").Single());
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers)]
        public async Task ResponseTrailers_MultipleValues_SentAsSeparateHeaders()
        {
            var response = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_MultipleValues_SentAsSeparateHeaders");

            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.NotEmpty(response.TrailingHeaders);

            Assert.Equal(new[] { "TrailerValue0", "TrailerValue1" }, response.TrailingHeaders.GetValues("TrailerName"));
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers)]
        public async Task ResponseTrailers_CompleteAsyncNoBody_TrailersSent()
        {
            // The app func for CompleteAsync will not finish until CompleteAsync_Completed is sent.
            // This verifies that the response is sent to the client with CompleteAsync
            var response = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_CompleteAsyncNoBody_TrailersSent");
            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.NotEmpty(response.TrailingHeaders);
            Assert.Equal("TrailerValue", response.TrailingHeaders.GetValues("TrailerName").Single());

            var response2 = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_CompleteAsyncNoBody_TrailersSent_Completed");
            Assert.True(response2.IsSuccessStatusCode);
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers)]
        public async Task ResponseTrailers_CompleteAsyncWithBody_TrailersSent()
        {
            // The app func for CompleteAsync will not finish until CompleteAsync_Completed is sent.
            // This verifies that the response is sent to the client with CompleteAsync
            var response = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_CompleteAsyncWithBody_TrailersSent");
            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Equal("Hello World", await response.Content.ReadAsStringAsync());
            Assert.NotEmpty(response.TrailingHeaders);
            Assert.Equal("Trailer Value", response.TrailingHeaders.GetValues("TrailerName").Single());

            var response2 = await SendRequestAsync(Fixture.Client.BaseAddress.ToString() + "ResponseTrailers_CompleteAsyncWithBody_TrailersSent_Completed");
            Assert.True(response2.IsSuccessStatusCode);
        }

        [ConditionalFact]
        [RequiresNewHandler]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersions.Win10, SkipReason = "Http2 requires Win10")]
        public async Task AppException_BeforeResponseHeaders_500()
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    await h2Connection.StartStreamAsync(1, GetHeaders("/AppException_BeforeResponseHeaders_500"), endStream: true);

                    await h2Connection.ReceiveHeadersAsync(1, decodedHeaders =>
                    {
                        Assert.Equal("500", decodedHeaders[HeaderNames.Status]);
                    });

                    var dataFrame = await h2Connection.ReceiveFrameAsync();
                    if (Environment.OSVersion.Version >= Win10_Regressed_DataFrame)
                    {
                        // TODO: Remove when the regression is fixed.
                        // https://github.com/dotnet/aspnetcore/issues/23164#issuecomment-652646163
                        Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: false, length: 0);

                        dataFrame = await h2Connection.ReceiveFrameAsync();
                    }
                    Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: true, length: 0);

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        [ConditionalFact]
        [RequiresNewHandler]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers, SkipReason = "Custom Reset support was added in Win10_20H2.")]
        public async Task AppException_AfterHeaders_ResetInternalError()
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    await h2Connection.StartStreamAsync(1, GetHeaders("/AppException_AfterHeaders_ResetInternalError"), endStream: true);

                    await h2Connection.ReceiveHeadersAsync(1, decodedHeaders =>
                    {
                        Assert.Equal("200", decodedHeaders[HeaderNames.Status]);
                    });

                    var frame = await h2Connection.ReceiveFrameAsync();
                    if (Environment.OSVersion.Version >= Win10_Regressed_DataFrame)
                    {
                        // TODO: Remove when the regression is fixed.
                        // https://github.com/dotnet/aspnetcore/issues/23164#issuecomment-652646163
                        Http2Utilities.VerifyDataFrame(frame, 1, endOfStream: false, length: 0);

                        frame = await h2Connection.ReceiveFrameAsync();
                    }
                    Http2Utilities.VerifyResetFrame(frame, expectedStreamId: 1, Http2ErrorCode.INTERNAL_ERROR);

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        [ConditionalFact]
        [RequiresNewHandler]
        public async Task Reset_Http1_NotSupported()
        {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            using HttpClient client = new HttpClient(handler);
            client.DefaultRequestVersion = HttpVersion.Version11;
            var response = await client.GetStringAsync(Fixture.Client.BaseAddress + "Reset_Http1_NotSupported");
            Assert.Equal("Hello World", response);
        }

        [ConditionalFact]
        [RequiresNewHandler]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersions.Win10, SkipReason = "Http2 requires Win10")]
        [MaximumOSVersion(OperatingSystems.Windows, WindowsVersions.Win10_20H1, SkipReason = "This is last version without Reset support")]
        public async Task Reset_PriorOSVersions_NotSupported()
        {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            using HttpClient client = new HttpClient(handler);
            client.DefaultRequestVersion = HttpVersion.Version20;
            var response = await client.GetStringAsync(Fixture.Client.BaseAddress + "Reset_PriorOSVersions_NotSupported");
            Assert.Equal("Hello World", response);
        }

        [ConditionalFact]
        [RequiresNewHandler]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers, SkipReason = "Reset support was added in Win10_20H2.")]
        public async Task Reset_BeforeResponse_Resets()
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    await h2Connection.StartStreamAsync(1, GetHeaders("/Reset_BeforeResponse_Resets"), endStream: true);

                    var resetFrame = await h2Connection.ReceiveFrameAsync();
                    Http2Utilities.VerifyResetFrame(resetFrame, expectedStreamId: 1, expectedErrorCode: (Http2ErrorCode)1111);

                    // Any app errors?
                    var client = CreateClient();
                    var response = await client.GetAsync(Fixture.Client.BaseAddress + "/Reset_BeforeResponse_Resets_Complete");
                    Assert.True(response.IsSuccessStatusCode);

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        [ConditionalFact]
        [RequiresNewHandler]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers, SkipReason = "Reset support was added in Win10_20H2.")]
        public async Task Reset_BeforeResponse_Zero_Resets()
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    await h2Connection.StartStreamAsync(1, GetHeaders("/Reset_BeforeResponse_Zero_Resets"), endStream: true);

                    var resetFrame = await h2Connection.ReceiveFrameAsync();
                    Http2Utilities.VerifyResetFrame(resetFrame, expectedStreamId: 1, expectedErrorCode: (Http2ErrorCode)0);

                    // Any app errors?
                    var client = CreateClient();
                    var response = await client.GetAsync(Fixture.Client.BaseAddress + "/Reset_BeforeResponse_Zero_Resets_Complete");
                    Assert.True(response.IsSuccessStatusCode);

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        [ConditionalFact]
        [RequiresNewHandler]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers, SkipReason = "Reset support was added in Win10_20H2.")]
        public async Task Reset_AfterResponseHeaders_Resets()
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    await h2Connection.StartStreamAsync(1, GetHeaders("/Reset_AfterResponseHeaders_Resets"), endStream: true);

                    // Any app errors?
                    var client = CreateClient();
                    var response = await client.GetAsync(Fixture.Client.BaseAddress + "/Reset_AfterResponseHeaders_Resets_Complete");
                    Assert.True(response.IsSuccessStatusCode);

                    await h2Connection.ReceiveHeadersAsync(1, decodedHeaders =>
                    {
                        Assert.Equal("200", decodedHeaders[HeaderNames.Status]);
                    });

                    var dataFrame = await h2Connection.ReceiveFrameAsync();
                    Http2Utilities.VerifyDataFrame(dataFrame, expectedStreamId: 1, endOfStream: false, length: 0);

                    var resetFrame = await h2Connection.ReceiveFrameAsync();
                    Http2Utilities.VerifyResetFrame(resetFrame, expectedStreamId: 1, expectedErrorCode: (Http2ErrorCode)1111);

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        [ConditionalFact]
        [RequiresNewHandler]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers, SkipReason = "Reset support was added in Win10_20H2.")]
        public async Task Reset_DuringResponseBody_Resets()
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    await h2Connection.StartStreamAsync(1, GetHeaders("/Reset_DuringResponseBody_Resets"), endStream: true);

                    // This is currently flaky, can either receive header or reset at this point
                    var headerOrResetFrame = await h2Connection.ReceiveFrameAsync();
                    Assert.True(headerOrResetFrame.Type == Http2FrameType.HEADERS || headerOrResetFrame.Type == Http2FrameType.RST_STREAM);

                    if (headerOrResetFrame.Type == Http2FrameType.HEADERS)
                    {
                        var dataFrame = await h2Connection.ReceiveFrameAsync();
                        Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: false, length: 11);

                        var resetFrame = await h2Connection.ReceiveFrameAsync();
                        Http2Utilities.VerifyResetFrame(resetFrame, expectedStreamId: 1, expectedErrorCode: (Http2ErrorCode)1111);
                    }
                    else
                    {
                        Http2Utilities.VerifyResetFrame(headerOrResetFrame, expectedStreamId: 1, expectedErrorCode: (Http2ErrorCode)1111);
                    }

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }


        [ConditionalFact]
        [RequiresNewHandler]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers, SkipReason = "Reset support was added in Win10_20H2.")]
        public async Task Reset_BeforeRequestBody_Resets()
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    await h2Connection.StartStreamAsync(1, GetPostHeaders("/Reset_BeforeRequestBody_Resets"), endStream: false);

                    // Any app errors?
                    //Assert.Equal(0, await appResult.Task.DefaultTimeout());

                    var resetFrame = await h2Connection.ReceiveFrameAsync();
                    Http2Utilities.VerifyResetFrame(resetFrame, expectedStreamId: 1, expectedErrorCode: (Http2ErrorCode)1111);

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        [ConditionalFact]
        [RequiresNewHandler]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers, SkipReason = "Reset support was added in Win10_20H2.")]
        public async Task Reset_DuringRequestBody_Resets()
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    await h2Connection.StartStreamAsync(1, GetPostHeaders("/Reset_DuringRequestBody_Resets"), endStream: false);
                    await h2Connection.SendDataAsync(1, new byte[10], endStream: false);

                    // Any app errors?
                    //Assert.Equal(0, await appResult.Task.DefaultTimeout());

                    var resetFrame = await h2Connection.ReceiveFrameAsync();
                    Http2Utilities.VerifyResetFrame(resetFrame, expectedStreamId: 1, expectedErrorCode: (Http2ErrorCode)1111);

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers, SkipReason = "Reset support was added in Win10_20H2.")]
        public async Task Reset_AfterCompleteAsync_NoReset()
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    await h2Connection.StartStreamAsync(1, GetHeaders("/Reset_AfterCompleteAsync_NoReset"), endStream: true);

                    await h2Connection.ReceiveHeadersAsync(1, decodedHeaders =>
                    {
                        Assert.Equal("200", decodedHeaders[HeaderNames.Status]);
                    });

                    var dataFrame = await h2Connection.ReceiveFrameAsync();
                    Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: false, length: 11);

                    dataFrame = await h2Connection.ReceiveFrameAsync();
                    Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: true, length: 0);

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        [ConditionalFact]
        [MinimumOSVersion(OperatingSystems.Windows, WindowsVersionForTrailers, SkipReason = "Reset support was added in Win10_20H2.")]
        public async Task Reset_CompleteAsyncDuringRequestBody_Resets()
        {
            await new HostBuilder()
                .UseHttp2Cat(Fixture.Client.BaseAddress.AbsoluteUri, async h2Connection =>
                {
                    await h2Connection.InitializeConnectionAsync();

                    h2Connection.Logger.LogInformation("Initialized http2 connection. Starting stream 1.");

                    await h2Connection.StartStreamAsync(1, Http2Utilities.PostRequestHeaders, endStream: false);
                    await h2Connection.SendDataAsync(1, new byte[10], endStream: false);

                    await h2Connection.ReceiveHeadersAsync(1, decodedHeaders =>
                    {
                        Assert.Equal("200", decodedHeaders[HeaderNames.Status]);
                    });

                    var dataFrame = await h2Connection.ReceiveFrameAsync();
                    if (Environment.OSVersion.Version >= Win10_Regressed_DataFrame)
                    {
                        // TODO: Remove when the regression is fixed.
                        // https://github.com/dotnet/aspnetcore/issues/23164#issuecomment-652646163
                        Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: false, length: 0);

                        dataFrame = await h2Connection.ReceiveFrameAsync();
                    }
                    Http2Utilities.VerifyDataFrame(dataFrame, 1, endOfStream: true, length: 0);

                    var resetFrame = await h2Connection.ReceiveFrameAsync();
                    Http2Utilities.VerifyResetFrame(resetFrame, expectedStreamId: 1, expectedErrorCode: Http2ErrorCode.NO_ERROR);

                    h2Connection.Logger.LogInformation("Connection stopped.");
                })
                .Build().RunAsync();
        }

        private static List<KeyValuePair<string, string>> GetHeaders(string path)
        {
            var headers = Headers.ToList();

            var kvp = new KeyValuePair<string, string>(HeaderNames.Path, path);
            headers.Add(kvp);
            return headers;
        }

        private static List<KeyValuePair<string, string>> GetPostHeaders(string path)
        {
            var headers = PostRequestHeaders.ToList();

            var kvp = new KeyValuePair<string, string>(HeaderNames.Path, path);
            headers.Add(kvp);
            return headers;
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler();
            handler.MaxResponseHeadersLength = 128;
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            var client = new HttpClient(handler);
            return client;
        }

        private async Task<HttpResponseMessage> SendRequestAsync(string uri, bool http2 = true)
        {
            var handler = new HttpClientHandler();
            handler.MaxResponseHeadersLength = 128;
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            using var client = new HttpClient(handler);
            client.DefaultRequestVersion = http2 ? HttpVersion.Version20 : HttpVersion.Version11;
            return await client.GetAsync(uri);
        }

        private static readonly IEnumerable<KeyValuePair<string, string>> Headers = new[]
        {
            new KeyValuePair<string, string>(HeaderNames.Method, "GET"),
            new KeyValuePair<string, string>(HeaderNames.Scheme, "https"),
            new KeyValuePair<string, string>(HeaderNames.Authority, "localhost:443"),
            new KeyValuePair<string, string>("user-agent", "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:54.0) Gecko/20100101 Firefox/54.0"),
            new KeyValuePair<string, string>("accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"),
            new KeyValuePair<string, string>("accept-language", "en-US,en;q=0.5"),
            new KeyValuePair<string, string>("accept-encoding", "gzip, deflate, br"),
            new KeyValuePair<string, string>("upgrade-insecure-requests", "1"),
        };

        private static readonly IEnumerable<KeyValuePair<string, string>> PostRequestHeaders = new[]
        {
            new KeyValuePair<string, string>(HeaderNames.Method, "POST"),
            new KeyValuePair<string, string>(HeaderNames.Scheme, "https"),
            new KeyValuePair<string, string>(HeaderNames.Authority, "localhost:80"),
        };
    }
}
