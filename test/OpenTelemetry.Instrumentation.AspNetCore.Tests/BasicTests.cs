// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Instrumentation.AspNetCore.Implementation;
using OpenTelemetry.Tests;
using OpenTelemetry.Trace;
using TestApp.AspNetCore;
using TestApp.AspNetCore.Filters;
using Xunit;
using Uri = System.Uri;

namespace OpenTelemetry.Instrumentation.AspNetCore.Tests;

// See https://github.com/aspnet/Docs/tree/master/aspnetcore/test/integration-tests/samples/2.x/IntegrationTestsSample
[Collection("AspNetCore")]
public sealed class BasicTests
    : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> factory;
    private TracerProvider? tracerProvider;

    public BasicTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void AddAspNetCoreInstrumentation_BadArgs()
    {
        TracerProviderBuilder? builder = null;
        Assert.Throws<ArgumentNullException>(() => builder!.AddAspNetCoreInstrumentation());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StatusIsUnsetOn200Response(bool disableLogging)
    {
        var exportedItems = new List<Activity>();
        void ConfigureTestServices(IServiceCollection services)
        {
            this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddAspNetCoreInstrumentation()
                .AddInMemoryExporter(exportedItems)
                .Build();
        }

        // Arrange
        using (var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(ConfigureTestServices);
                if (disableLogging)
                {
                    builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
                }
            })
            .CreateClient())
        {
            // Act
            using var response = await client.GetAsync(new Uri("/api/values", UriKind.Relative));

            // Assert
            response.EnsureSuccessStatusCode(); // Status Code 200-299

            WaitForActivityExport(exportedItems, 1);
        }

        Assert.Single(exportedItems);
        var activity = exportedItems[0];

        Assert.Equal(200, activity.GetTagValue(SemanticConventions.AttributeHttpResponseStatusCode));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        ValidateAspNetCoreActivity(activity, "/api/values");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessfulTemplateControllerCallGeneratesASpan(bool shouldEnrich)
    {
        var exportedItems = new List<Activity>();
        void ConfigureTestServices(IServiceCollection services)
        {
            this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddAspNetCoreInstrumentation(options =>
                {
                    if (shouldEnrich)
                    {
                        options.EnrichWithHttpRequest = (activity, request) => { activity.SetTag("enrichedOnStart", "yes"); };
                        options.EnrichWithHttpResponse = (activity, response) => { activity.SetTag("enrichedOnStop", "yes"); };
                    }
                })
                .AddInMemoryExporter(exportedItems)
                .Build();
        }

        // Arrange
        using (var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(ConfigureTestServices);
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient())
        {
            // Act
            using var response = await client.GetAsync(new Uri("/api/values", UriKind.Relative));

            // Assert
            response.EnsureSuccessStatusCode(); // Status Code 200-299

            WaitForActivityExport(exportedItems, 1);
        }

        Assert.Single(exportedItems);
        var activity = exportedItems[0];

        if (shouldEnrich)
        {
            Assert.Contains(activity.Tags, tag => tag.Key == "enrichedOnStart" && tag.Value == "yes");
            Assert.Contains(activity.Tags, tag => tag.Key == "enrichedOnStop" && tag.Value == "yes");
        }

        ValidateAspNetCoreActivity(activity, "/api/values");
    }

    [Fact]
    public async Task SuccessfulTemplateControllerCallUsesParentContext()
    {
        var exportedItems = new List<Activity>();
        var expectedTraceId = ActivityTraceId.CreateRandom();
        var expectedSpanId = ActivitySpanId.CreateRandom();

        // Arrange
        using (var testFactory = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                    .AddAspNetCoreInstrumentation()
                    .AddInMemoryExporter(exportedItems)
                    .Build();
                });

                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            }))
        {
            using var client = testFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/values/2");
            request.Headers.Add("traceparent", $"00-{expectedTraceId}-{expectedSpanId}-01");

            // Act
            var response = await client.SendAsync(request);

            // Assert
            response.EnsureSuccessStatusCode(); // Status Code 200-299

            WaitForActivityExport(exportedItems, 1);
        }

        Assert.Single(exportedItems);
        var activity = exportedItems[0];

        Assert.Equal("Microsoft.AspNetCore.Hosting.HttpRequestIn", activity.OperationName);

        Assert.Equal(expectedTraceId, activity.Context.TraceId);
        Assert.Equal(expectedSpanId, activity.ParentSpanId);

        ValidateAspNetCoreActivity(activity, "/api/values/2");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CustomPropagator(bool addSampler)
    {
        try
        {
            var exportedItems = new List<Activity>();
            var expectedTraceId = ActivityTraceId.CreateRandom();
            var expectedSpanId = ActivitySpanId.CreateRandom();

            var propagator = new CustomTextMapPropagator
            {
                TraceId = expectedTraceId,
                SpanId = expectedSpanId,
            };

            // Arrange
            using (var testFactory = this.factory
                .WithWebHostBuilder(builder =>
                    {
                        builder.ConfigureTestServices(services =>
                        {
                            Sdk.SetDefaultTextMapPropagator(propagator);
                            var tracerProviderBuilder = Sdk.CreateTracerProviderBuilder();

                            if (addSampler)
                            {
                                tracerProviderBuilder
                                    .SetSampler(new TestSampler(SamplingDecision.RecordAndSample, new Dictionary<string, object> { { "SomeTag", "SomeKey" }, }));
                            }

                            this.tracerProvider = tracerProviderBuilder
                                                    .AddAspNetCoreInstrumentation()
                                                    .AddInMemoryExporter(exportedItems)
                                                    .Build();
                        });
                        builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
                    }))
            {
                using var client = testFactory.CreateClient();
                using var response = await client.GetAsync(new Uri("/api/values/2", UriKind.Relative));
                response.EnsureSuccessStatusCode(); // Status Code 200-299

                WaitForActivityExport(exportedItems, 1);
            }

            Assert.Single(exportedItems);
            var activity = exportedItems[0];

            Assert.True(activity.Duration != TimeSpan.Zero);

            Assert.Equal(expectedTraceId, activity.Context.TraceId);
            Assert.Equal(expectedSpanId, activity.ParentSpanId);

            ValidateAspNetCoreActivity(activity, "/api/values/2");
        }
        finally
        {
            Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator([new TraceContextPropagator(), new BaggagePropagator()]));
        }
    }

    [Fact]
    public async Task RequestNotCollectedWhenFilterIsApplied()
    {
        var exportedItems = new List<Activity>();

        void ConfigureTestServices(IServiceCollection services)
        {
            this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddAspNetCoreInstrumentation((opt) => opt.Filter = (ctx) => ctx.Request.Path != "/api/values/2")
                .AddInMemoryExporter(exportedItems)
                .Build();
        }

        // Arrange
        using (var testFactory = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(ConfigureTestServices);
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            }))
        {
            using var client = testFactory.CreateClient();

            // Act
            using var response1 = await client.GetAsync(new Uri("/api/values", UriKind.Relative));
            using var response2 = await client.GetAsync(new Uri("/api/values/2", UriKind.Relative));

            // Assert
            response1.EnsureSuccessStatusCode(); // Status Code 200-299
            response2.EnsureSuccessStatusCode(); // Status Code 200-299

            WaitForActivityExport(exportedItems, 1);
        }

        Assert.Single(exportedItems);
        var activity = exportedItems[0];

        ValidateAspNetCoreActivity(activity, "/api/values");
    }

    [Fact]
    public async Task RequestNotCollectedWhenFilterThrowException()
    {
        var exportedItems = new List<Activity>();

        void ConfigureTestServices(IServiceCollection services)
        {
            this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddAspNetCoreInstrumentation((opt) => opt.Filter = (ctx) =>
                {
                    return ctx.Request.Path == "/api/values/2"
                        ? throw new Exception("from InstrumentationFilter")
                        : true;
                })
                .AddInMemoryExporter(exportedItems)
                .Build();
        }

        // Arrange
        using (var testFactory = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(ConfigureTestServices);
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            }))
        {
            using var client = testFactory.CreateClient();

            // Act
            using (var inMemoryEventListener = new InMemoryEventListener(AspNetCoreInstrumentationEventSource.Log))
            {
                using var response1 = await client.GetAsync(new Uri("/api/values", UriKind.Relative));
                using var response2 = await client.GetAsync(new Uri("/api/values/2", UriKind.Relative));

                response1.EnsureSuccessStatusCode(); // Status Code 200-299
                response2.EnsureSuccessStatusCode(); // Status Code 200-299
                Assert.Single(inMemoryEventListener.Events, e => e.EventId == 3);
            }

            WaitForActivityExport(exportedItems, 1);
        }

        // As InstrumentationFilter threw, we continue as if the
        // InstrumentationFilter did not exist.

        Assert.Single(exportedItems);
        var activity = exportedItems[0];
        ValidateAspNetCoreActivity(activity, "/api/values");
    }

    [Theory]
    [InlineData(SamplingDecision.Drop)]
    [InlineData(SamplingDecision.RecordOnly)]
    [InlineData(SamplingDecision.RecordAndSample)]
    public async Task ExtractContextIrrespectiveOfSamplingDecision(SamplingDecision samplingDecision)
    {
        try
        {
            var expectedTraceId = ActivityTraceId.CreateRandom();
            var expectedParentSpanId = ActivitySpanId.CreateRandom();
            var expectedTraceState = "rojo=1,congo=2";
            var activityContext = new ActivityContext(expectedTraceId, expectedParentSpanId, ActivityTraceFlags.Recorded, expectedTraceState, true);
            var expectedBaggage = Baggage.SetBaggage("key1", "value1").SetBaggage("key2", "value2");
            Sdk.SetDefaultTextMapPropagator(new ExtractOnlyPropagator(activityContext, expectedBaggage));

            // Arrange
            using var testFactory = this.factory
                .WithWebHostBuilder(builder =>
                    {
                        builder.ConfigureTestServices(services => { this.tracerProvider = Sdk.CreateTracerProviderBuilder().SetSampler(new TestSampler(samplingDecision)).AddAspNetCoreInstrumentation().Build(); });
                        builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
                    });
            using var client = testFactory.CreateClient();

            // Test TraceContext Propagation
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/GetChildActivityTraceContext");
            var response = await client.SendAsync(request);
            var childActivityTraceContext = JsonSerializer.Deserialize<Dictionary<string, string>>(await response.Content.ReadAsStringAsync());

            response.EnsureSuccessStatusCode();

            Assert.NotNull(childActivityTraceContext);
            Assert.Equal(expectedTraceId.ToString(), childActivityTraceContext["TraceId"]);
            Assert.Equal(expectedTraceState, childActivityTraceContext["TraceState"]);
            Assert.NotEqual(expectedParentSpanId.ToString(), childActivityTraceContext["ParentSpanId"]); // there is a new activity created in instrumentation therefore the ParentSpanId is different that what is provided in the headers

            // Test Baggage Context Propagation
            request = new HttpRequestMessage(HttpMethod.Get, "/api/GetChildActivityBaggageContext");

            response = await client.SendAsync(request);
            var childActivityBaggageContext = JsonSerializer.Deserialize<IReadOnlyDictionary<string, string>>(await response.Content.ReadAsStringAsync());

            response.EnsureSuccessStatusCode();

            Assert.NotNull(childActivityBaggageContext);
            Assert.Single(childActivityBaggageContext, item => item.Key == "key1" && item.Value == "value1");
            Assert.Single(childActivityBaggageContext, item => item.Key == "key2" && item.Value == "value2");
        }
        finally
        {
            Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator([new TraceContextPropagator(), new BaggagePropagator()]));
        }
    }

    [Fact]
    public async Task ExtractContextIrrespectiveOfTheFilterApplied()
    {
        try
        {
            var expectedTraceId = ActivityTraceId.CreateRandom();
            var expectedParentSpanId = ActivitySpanId.CreateRandom();
            var expectedTraceState = "rojo=1,congo=2";
            var activityContext = new ActivityContext(expectedTraceId, expectedParentSpanId, ActivityTraceFlags.Recorded, expectedTraceState);
            var expectedBaggage = Baggage.SetBaggage("key1", "value1").SetBaggage("key2", "value2");
            Sdk.SetDefaultTextMapPropagator(new ExtractOnlyPropagator(activityContext, expectedBaggage));

            // Arrange
            var isFilterCalled = false;
            using var testFactory = this.factory
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureTestServices(services =>
                    {
                        this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                            .AddAspNetCoreInstrumentation(options =>
                            {
                                options.Filter = context =>
                                {
                                    isFilterCalled = true;
                                    return false;
                                };
                            })
                            .Build();
                    });
                    builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
                });
            using var client = testFactory.CreateClient();

            // Test TraceContext Propagation
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/GetChildActivityTraceContext");
            var response = await client.SendAsync(request);

            // Ensure that filter was called
            Assert.True(isFilterCalled);

            var childActivityTraceContext = JsonSerializer.Deserialize<Dictionary<string, string>>(await response.Content.ReadAsStringAsync());

            response.EnsureSuccessStatusCode();

            Assert.NotNull(childActivityTraceContext);
            Assert.Equal(expectedTraceId.ToString(), childActivityTraceContext["TraceId"]);
            Assert.Equal(expectedTraceState, childActivityTraceContext["TraceState"]);
            Assert.NotEqual(expectedParentSpanId.ToString(), childActivityTraceContext["ParentSpanId"]); // there is a new activity created in instrumentation therefore the ParentSpanId is different that what is provided in the headers

            // Test Baggage Context Propagation
            request = new HttpRequestMessage(HttpMethod.Get, "/api/GetChildActivityBaggageContext");

            response = await client.SendAsync(request);
            var childActivityBaggageContext = JsonSerializer.Deserialize<IReadOnlyDictionary<string, string>>(await response.Content.ReadAsStringAsync());

            response.EnsureSuccessStatusCode();

            Assert.NotNull(childActivityBaggageContext);
            Assert.Single(childActivityBaggageContext, item => item.Key == "key1" && item.Value == "value1");
            Assert.Single(childActivityBaggageContext, item => item.Key == "key2" && item.Value == "value2");
        }
        finally
        {
            Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator([new TraceContextPropagator(), new BaggagePropagator()]));
        }
    }

    [Fact]
    public async Task BaggageIsNotClearedWhenActivityStopped()
    {
        int? baggageCountAfterStart = null;
        int? baggageCountAfterStop = null;
        using var stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset);

        void ConfigureTestServices(IServiceCollection services)
        {
            this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddAspNetCoreInstrumentation(
                    new TestHttpInListener(new AspNetCoreTraceInstrumentationOptions())
                    {
                        OnEventWrittenCallback = (name, payload) =>
                        {
                            switch (name)
                            {
                                case HttpInListener.OnStartEvent:
                                    {
                                        baggageCountAfterStart = Baggage.Current.Count;
                                    }

                                    break;
                                case HttpInListener.OnStopEvent:
                                    {
                                        baggageCountAfterStop = Baggage.Current.Count;
                                        stopSignal.Set();
                                    }

                                    break;
                                default:
                                    break;
                            }
                        },
                    })
                .Build();
        }

        // Arrange
        using (var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(ConfigureTestServices);
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient())
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/values");

            request.Headers.TryAddWithoutValidation("baggage", "TestKey1=123,TestKey2=456");

            // Act
            using var response = await client.SendAsync(request);
        }

        stopSignal.WaitOne(5000);

        // Assert
        Assert.NotNull(baggageCountAfterStart);
        Assert.Equal(2, baggageCountAfterStart);
        Assert.NotNull(baggageCountAfterStop);
        Assert.Equal(2, baggageCountAfterStop);
    }

    [Theory]
    [InlineData(SamplingDecision.Drop, false, false)]
    [InlineData(SamplingDecision.RecordOnly, true, true)]
    [InlineData(SamplingDecision.RecordAndSample, true, true)]
    public async Task FilterAndEnrichAreOnlyCalledWhenSampled(SamplingDecision samplingDecision, bool shouldFilterBeCalled, bool shouldEnrichBeCalled)
    {
        var filterCalled = false;
        var enrichWithHttpRequestCalled = false;
        var enrichWithHttpResponseCalled = false;
        void ConfigureTestServices(IServiceCollection services)
        {
            this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetSampler(new TestSampler(samplingDecision))
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = (context) =>
                    {
                        filterCalled = true;
                        return true;
                    };
                    options.EnrichWithHttpRequest = (activity, request) =>
                    {
                        enrichWithHttpRequestCalled = true;
                    };
                    options.EnrichWithHttpResponse = (activity, request) =>
                    {
                        enrichWithHttpResponseCalled = true;
                    };
                })
                .Build();
        }

        // Arrange
        using var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(ConfigureTestServices);
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient();

        // Act
        using var response = await client.GetAsync(new Uri("/api/values", UriKind.Relative));

        // Assert
        Assert.Equal(shouldFilterBeCalled, filterCalled);
        Assert.Equal(shouldEnrichBeCalled, enrichWithHttpRequestCalled);
        Assert.Equal(shouldEnrichBeCalled, enrichWithHttpResponseCalled);
    }

    [Fact]
    public async Task ActivitiesStartedInMiddlewareShouldNotBeUpdated()
    {
        var exportedItems = new List<Activity>();

        var activitySourceName = "TestMiddlewareActivitySource";
        var activityName = "TestMiddlewareActivity";

        void ConfigureTestServices(IServiceCollection services)
        {
            services.AddSingleton<TestActivityMiddleware>(new TestTestActivityMiddleware(activitySourceName, activityName));
            this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddAspNetCoreInstrumentation()
                .AddSource(activitySourceName)
                .AddInMemoryExporter(exportedItems)
                .Build();
        }

        // Arrange
        using (var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(ConfigureTestServices);
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient())
        {
            using var response = await client.GetAsync(new Uri("/api/values/2", UriKind.Relative));
            response.EnsureSuccessStatusCode();
            WaitForActivityExport(exportedItems, 2);
        }

        Assert.Equal(2, exportedItems.Count);

        var middlewareActivity = exportedItems[0];

        var aspnetcoreframeworkactivity = exportedItems[1];

        // Middleware activity name should not be changed
        Assert.Equal(ActivityKind.Internal, middlewareActivity.Kind);
        Assert.Equal(activityName, middlewareActivity.OperationName);
        Assert.Equal(activityName, middlewareActivity.DisplayName);

        // tag http.method should be added on activity started by asp.net core
        Assert.Equal("GET", aspnetcoreframeworkactivity.GetTagValue(SemanticConventions.AttributeHttpRequestMethod) as string);
        Assert.Equal("Microsoft.AspNetCore.Hosting.HttpRequestIn", aspnetcoreframeworkactivity.OperationName);
    }

    [Theory]
    [InlineData("CONNECT", "CONNECT", null, "CONNECT")]
    [InlineData("DELETE", "DELETE", null, "DELETE")]
    [InlineData("GET", "GET", null, "GET")]
    [InlineData("PUT", "PUT", null, "PUT")]
    [InlineData("HEAD", "HEAD", null, "HEAD")]
    [InlineData("OPTIONS", "OPTIONS", null, "OPTIONS")]
    [InlineData("PATCH", "PATCH", null, "PATCH")]
    [InlineData("Get", "GET", "Get", "GET")]
    [InlineData("POST", "POST", null, "POST")]
    [InlineData("TRACE", "TRACE", null, "TRACE")]
    [InlineData("CUSTOM", "_OTHER", "CUSTOM", "HTTP")]
    public async Task HttpRequestMethodAndActivityDisplayIsSetAsPerSpec(string originalMethod, string expectedMethod, string? expectedOriginalMethod, string expectedDisplayName)
    {
        var exportedItems = new List<Activity>();

        void ConfigureTestServices(IServiceCollection services)
        {
            this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddAspNetCoreInstrumentation()
                .AddInMemoryExporter(exportedItems)
                .Build();
        }

        // Arrange
        using var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(ConfigureTestServices);
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient();

        var message = new HttpRequestMessage
        {
            Method = new HttpMethod(originalMethod),
        };

        try
        {
            using var response = await client.SendAsync(message);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // ignore error.
        }

        WaitForActivityExport(exportedItems, 1);

        Assert.Single(exportedItems);

        var activity = exportedItems[0];

        Assert.Equal(expectedMethod, activity.GetTagValue(SemanticConventions.AttributeHttpRequestMethod));
        Assert.Equal(expectedOriginalMethod, activity.GetTagValue(SemanticConventions.AttributeHttpRequestMethodOriginal));
        Assert.Equal(expectedDisplayName, activity.DisplayName);
    }

    [Fact]
    public async Task ActivitiesStartedInMiddlewareBySettingHostActivityToNullShouldNotBeUpdated()
    {
        var exportedItems = new List<Activity>();

        var activitySourceName = "TestMiddlewareActivitySource";
        var activityName = "TestMiddlewareActivity";

        // Arrange
        using (var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices((IServiceCollection services) =>
                {
                    services.AddSingleton<TestActivityMiddleware>(new TestNullHostActivityMiddlewareImpl(activitySourceName, activityName));
                    services.AddOpenTelemetry()
                        .WithTracing(builder => builder
                            .AddAspNetCoreInstrumentation()
                            .AddSource(activitySourceName)
                            .AddInMemoryExporter(exportedItems));
                });
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient())
        {
            using var response = await client.GetAsync(new Uri("/api/values/2", UriKind.Relative));
            response.EnsureSuccessStatusCode();
            WaitForActivityExport(exportedItems, 2);
        }

        Assert.Equal(2, exportedItems.Count);

        var middlewareActivity = exportedItems[0];

        var aspnetcoreframeworkactivity = exportedItems[1];

        // Middleware activity name should not be changed
        Assert.Equal(ActivityKind.Internal, middlewareActivity.Kind);
        Assert.Equal(activityName, middlewareActivity.OperationName);
        Assert.Equal(activityName, middlewareActivity.DisplayName);

        // tag http.method should be added on activity started by asp.net core
        Assert.Equal("GET", aspnetcoreframeworkactivity.GetTagValue(SemanticConventions.AttributeHttpRequestMethod) as string);
        Assert.Equal("Microsoft.AspNetCore.Hosting.HttpRequestIn", aspnetcoreframeworkactivity.OperationName);
    }

#if NET
    [Fact]
    public async Task UserRegisteredActivitySourceIsUsedForActivityCreationByAspNetCore()
    {
        var exportedItems = new List<Activity>();
        void ConfigureTestServices(IServiceCollection services)
        {
            services.AddOpenTelemetry()
                .WithTracing(builder => builder
                    .AddAspNetCoreInstrumentation()
                    .AddInMemoryExporter(exportedItems));

            // Register ActivitySource here so that it will be used
            // by ASP.NET Core to create activities
            // https://github.com/dotnet/aspnetcore/blob/0e5cbf447d329a1e7d69932c3decd1c70a00fbba/src/Hosting/Hosting/src/Internal/WebHost.cs#L152
            services.AddSingleton(sp => new ActivitySource("UserRegisteredActivitySource"));
        }

        // Arrange
        using (var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(ConfigureTestServices);
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient())
        {
            // Act
            using var response = await client.GetAsync(new Uri("/api/values", UriKind.Relative));

            // Assert
            response.EnsureSuccessStatusCode(); // Status Code 200-299

            WaitForActivityExport(exportedItems, 1);
        }

        Assert.Single(exportedItems);
        var activity = exportedItems[0];

        Assert.Equal("UserRegisteredActivitySource", activity.Source.Name);
    }
#endif

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShouldExportActivityWithOneOrMoreExceptionFilters(int mode)
    {
        var exportedItems = new List<Activity>();

        // Arrange
        using (var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(
                (s) => this.ConfigureExceptionFilters(s, mode, ref exportedItems));
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient())
        {
            // Act
            using var response = await client.GetAsync(new Uri("/api/error", UriKind.Relative));

            WaitForActivityExport(exportedItems, 1);
        }

        // Assert
        AssertException(exportedItems);
    }

    [Fact]
    public async Task DiagnosticSourceCallbacksAreReceivedOnlyForSubscribedEvents()
    {
        var numberOfUnSubscribedEvents = 0;
        var numberofSubscribedEvents = 0;

        this.tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddAspNetCoreInstrumentation(
                new TestHttpInListener(new AspNetCoreTraceInstrumentationOptions())
                {
                    OnEventWrittenCallback = (name, payload) =>
                    {
                        switch (name)
                        {
                            case HttpInListener.OnStartEvent:
                                {
                                    numberofSubscribedEvents++;
                                }

                                break;
                            case HttpInListener.OnStopEvent:
                                {
                                    numberofSubscribedEvents++;
                                }

                                break;
                            default:
                                {
                                    numberOfUnSubscribedEvents++;
                                }

                                break;
                        }
                    },
                })
            .Build();

        // Arrange
        using (var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient())
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/values");

            // Act
            using var response = await client.SendAsync(request);
        }

        Assert.Equal(0, numberOfUnSubscribedEvents);
        Assert.Equal(2, numberofSubscribedEvents);
    }

    [Fact]
    public async Task DiagnosticSourceExceptionCallbackIsReceivedForUnHandledException()
    {
        var numberOfUnSubscribedEvents = 0;
        var numberofSubscribedEvents = 0;
        var numberOfExceptionCallbacks = 0;

        this.tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddAspNetCoreInstrumentation(
                new TestHttpInListener(new AspNetCoreTraceInstrumentationOptions())
                {
                    OnEventWrittenCallback = (name, payload) =>
                    {
                        switch (name)
                        {
                            case HttpInListener.OnStartEvent:
                                {
                                    numberofSubscribedEvents++;
                                }

                                break;
                            case HttpInListener.OnStopEvent:
                                {
                                    numberofSubscribedEvents++;
                                }

                                break;

                            // TODO: Add test case for validating name for both the types
                            // of exception event.
                            case HttpInListener.OnUnhandledHostingExceptionEvent:
                            case HttpInListener.OnUnHandledDiagnosticsExceptionEvent:
                                {
                                    numberofSubscribedEvents++;
                                    numberOfExceptionCallbacks++;
                                }

                                break;
                            default:
                                {
                                    numberOfUnSubscribedEvents++;
                                }

                                break;
                        }
                    },
                })
            .Build();

        // Arrange
        using (var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient())
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/error");

                // Act
                using var response = await client.SendAsync(request);
            }
            catch
            {
                // ignore exception
            }
        }

        Assert.Equal(1, numberOfExceptionCallbacks);
        Assert.Equal(0, numberOfUnSubscribedEvents);
        Assert.Equal(3, numberofSubscribedEvents);
    }

    [Fact]
    public async Task DiagnosticSourceExceptionCallBackIsNotReceivedForExceptionsHandledInMiddleware()
    {
        var numberOfUnSubscribedEvents = 0;
        var numberOfSubscribedEvents = 0;
        var numberOfExceptionCallbacks = 0;
        var exceptionHandled = false;

        // configure SDK
        this.tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddAspNetCoreInstrumentation(
                new TestHttpInListener(new AspNetCoreTraceInstrumentationOptions())
                {
                    OnEventWrittenCallback = (name, payload) =>
                    {
                        switch (name)
                        {
                            case HttpInListener.OnStartEvent:
                                {
                                    numberOfSubscribedEvents++;
                                }

                                break;
                            case HttpInListener.OnStopEvent:
                                {
                                    numberOfSubscribedEvents++;
                                }

                                break;

                            // TODO: Add test case for validating name for both the types
                            // of exception event.
                            case HttpInListener.OnUnhandledHostingExceptionEvent:
                            case HttpInListener.OnUnHandledDiagnosticsExceptionEvent:
                                {
                                    numberOfSubscribedEvents++;
                                    numberOfExceptionCallbacks++;
                                }

                                break;
                            default:
                                {
                                    numberOfUnSubscribedEvents++;
                                }

                                break;
                        }
                    },
                })
                .Build();

        TestMiddleware.Create(builder => builder
            .UseExceptionHandler(handler =>
                handler.Run(async (ctx) =>
                {
                    exceptionHandled = true;
                    await ctx.Response.WriteAsync("handled");
                })));

        using (var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient())
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/error");
                using var response = await client.SendAsync(request);
            }
            catch
            {
                // ignore exception
            }
        }

        Assert.Equal(0, numberOfExceptionCallbacks);
        Assert.Equal(0, numberOfUnSubscribedEvents);
        Assert.Equal(2, numberOfSubscribedEvents);
        Assert.True(exceptionHandled);
    }

    [Fact]
    public async Task NoSiblingActivityCreatedWhenTraceFlagsNone()
    {
        using var localTracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetSampler(new AlwaysOnSampler())
            .AddAspNetCoreInstrumentation()
            .Build();

        using var testFactory = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                    .AddAspNetCoreInstrumentation()
                    .Build();
                });

                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            });
        using var client = testFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/GetActivityEquality");
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        request.Headers.Add("traceparent", $"00-{traceId}-{spanId}-00");

        var response = await client.SendAsync(request);
        var result = bool.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(response.IsSuccessStatusCode);

        // Confirm that Activity.Current and IHttpActivityFeature activity are same
        Assert.True(result);
    }

    [Theory]
    [InlineData("?a", "?a", false)]
    [InlineData("?a=bdjdjh", "?a=Redacted", false)]
    [InlineData("?a=b&", "?a=Redacted&", false)]
    [InlineData("?c=b&", "?c=Redacted&", false)]
    [InlineData("?c=a", "?c=Redacted", false)]
    [InlineData("?a=b&c", "?a=Redacted&c", false)]
    [InlineData("?a=b&c=1123456&", "?a=Redacted&c=Redacted&", false)]
    [InlineData("?a=b&c=1&a1", "?a=Redacted&c=Redacted&a1", false)]
    [InlineData("?a=ghgjgj&c=1deedd&a1=", "?a=Redacted&c=Redacted&a1=Redacted", false)]
    [InlineData("?a=b&c=11&a1=&", "?a=Redacted&c=Redacted&a1=Redacted&", false)]
    [InlineData("?c&c&c&", "?c&c&c&", false)]
    [InlineData("?a&a&a&a", "?a&a&a&a", false)]
    [InlineData("?&&&&&&&", "?&&&&&&&", false)]
    [InlineData("?c", "?c", false)]
    [InlineData("?a", "?a", true)]
    [InlineData("?a=bdfdfdf", "?a=bdfdfdf", true)]
    [InlineData("?a=b&", "?a=b&", true)]
    [InlineData("?c=b&", "?c=b&", true)]
    [InlineData("?c=a", "?c=a", true)]
    [InlineData("?a=b&c", "?a=b&c", true)]
    [InlineData("?a=b&c=111111&", "?a=b&c=111111&", true)]
    [InlineData("?a=b&c=1&a1", "?a=b&c=1&a1", true)]
    [InlineData("?a=b&c=1&a1=", "?a=b&c=1&a1=", true)]
    [InlineData("?a=b123&c=11&a1=&", "?a=b123&c=11&a1=&", true)]
    [InlineData("?c&c&c&", "?c&c&c&", true)]
    [InlineData("?a&a&a&a", "?a&a&a&a", true)]
    [InlineData("?&&&&&&&", "?&&&&&&&", true)]
    [InlineData("?c", "?c", true)]
    [InlineData("?c=%26&", "?c=Redacted&", false)]
    public async Task ValidateUrlQueryRedaction(string urlQuery, string expectedUrlQuery, bool disableQueryRedaction)
    {
        var exportedItems = new List<Activity>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION"] = disableQueryRedaction.ToString() })
            .Build();

        var path = "/api/values" + urlQuery;

        // Arrange
        using var traceprovider = Sdk.CreateTracerProviderBuilder()
            .ConfigureServices(services => services.AddSingleton<IConfiguration>(configuration))
            .AddAspNetCoreInstrumentation()
            .AddInMemoryExporter(exportedItems)
            .Build();

        using (var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient())
        {
            try
            {
                using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            }
            catch (Exception)
            {
                // ignore errors
            }

            WaitForActivityExport(exportedItems, 1);
        }

        Assert.Single(exportedItems);
        var activity = exportedItems[0];

        Assert.Equal(expectedUrlQuery, activity.GetTagValue(SemanticConventions.AttributeUrlQuery));
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task SignalRActivitesAreListenedTo()
    {
        var exportedItems = new List<Activity>();
        void ConfigureTestServices(IServiceCollection services)
        {
            this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddAspNetCoreInstrumentation()
                .AddInMemoryExporter(exportedItems)
                .Build();
        }

        // Arrange
        using (var server = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(ConfigureTestServices);
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            }))
        {
            await using var client = new HubConnectionBuilder()
                .WithUrl(server.Server.BaseAddress + "testHub", o =>
                {
                    o.HttpMessageHandlerFactory = _ => server.Server.CreateHandler();
                    o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                }).Build();
            await client.StartAsync();

            await client.SendAsync("Send", "text");

            await client.StopAsync();
        }

        WaitForActivityExport(exportedItems, 10);

        var hubActivity = exportedItems
            .Where(a => a.DisplayName.StartsWith("TestApp.AspNetCore.TestHub", StringComparison.InvariantCulture));

        Assert.Equal(3, hubActivity.Count());
        Assert.Collection(
            hubActivity,
            one =>
            {
                Assert.Equal("TestApp.AspNetCore.TestHub/OnConnectedAsync", one.DisplayName);
            },
            two =>
            {
                Assert.Equal("TestApp.AspNetCore.TestHub/Send", two.DisplayName);
            },
            three =>
            {
                Assert.Equal("TestApp.AspNetCore.TestHub/OnDisconnectedAsync", three.DisplayName);
            });
    }

    [Fact]
    public async Task SignalRActivitesCanBeDisabled()
    {
        var exportedItems = new List<Activity>();
        void ConfigureTestServices(IServiceCollection services)
        {
            this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddAspNetCoreInstrumentation(o => o.EnableAspNetCoreSignalRSupport = false)
                .AddInMemoryExporter(exportedItems)
                .Build();
        }

        // Arrange
        using (var server = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(ConfigureTestServices);
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            }))
        {
            await using var client = new HubConnectionBuilder()
                .WithUrl(server.Server.BaseAddress + "testHub", o =>
                {
                    o.HttpMessageHandlerFactory = _ => server.Server.CreateHandler();
                    o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                }).Build();
            await client.StartAsync();

            await client.SendAsync("Send", "text");

            await client.StopAsync();
        }

        WaitForActivityExport(exportedItems, 8);

        var hubActivity = exportedItems
            .Where(a => a.DisplayName.StartsWith("TestApp.AspNetCore.TestHub", StringComparison.InvariantCulture));

        Assert.Empty(hubActivity);
    }
#endif

    public void Dispose()
    {
        this.tracerProvider?.Dispose();
    }

    private static void WaitForActivityExport(List<Activity> exportedItems, int count)
    {
        // We need to let End callback execute as it is executed AFTER response was returned.
        // In unit tests environment there may be a lot of parallel unit tests executed, so
        // giving some breezing room for the End callback to complete
        Assert.True(
            SpinWait.SpinUntil(
            () =>
            {
                Thread.Sleep(10);
                return exportedItems.Count >= count;
            },
            TimeSpan.FromSeconds(1)),
            $"Actual: {exportedItems.Count} Expected: {count}");
    }

    private static void ValidateAspNetCoreActivity(Activity activityToValidate, string expectedHttpPath)
    {
        Assert.Equal(ActivityKind.Server, activityToValidate.Kind);
#if NET
        Assert.Equal(HttpInListener.AspNetCoreActivitySourceName, activityToValidate.Source.Name);
        Assert.NotNull(activityToValidate.Source.Version);
        Assert.Empty(activityToValidate.Source.Version);
#else
        Assert.Equal(HttpInListener.ActivitySourceName, activityToValidate.Source.Name);
        Assert.Equal(HttpInListener.Version.ToString(), activityToValidate.Source.Version);
#endif
        Assert.Equal(expectedHttpPath, activityToValidate.GetTagValue(SemanticConventions.AttributeUrlPath) as string);
    }

    private static void AssertException(List<Activity> exportedItems)
    {
        Assert.Single(exportedItems);
        var activity = exportedItems[0];

        var exMessage = "something's wrong!";
        Assert.Single(activity.Events);
        Assert.Equal("System.Exception", activity.Events.First().Tags.FirstOrDefault(t => t.Key == SemanticConventions.AttributeExceptionType).Value);
        Assert.Equal(exMessage, activity.Events.First().Tags.FirstOrDefault(t => t.Key == SemanticConventions.AttributeExceptionMessage).Value);

        ValidateAspNetCoreActivity(activity, "/api/error");
    }

    private void ConfigureExceptionFilters(IServiceCollection services, int mode, ref List<Activity> exportedItems)
    {
        switch (mode)
        {
            case 1:
                services.AddMvc(x => x.Filters.Add<ExceptionFilter1>());
                break;
            case 2:
                services.AddMvc(x => x.Filters.Add<ExceptionFilter1>());
                services.AddMvc(x => x.Filters.Add<ExceptionFilter2>());
                break;
            default:
                break;
        }

        this.tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddAspNetCoreInstrumentation(x => x.RecordException = true)
            .AddInMemoryExporter(exportedItems)
            .Build();
    }

    private class ExtractOnlyPropagator(ActivityContext activityContext, Baggage baggage) : TextMapPropagator
    {
        private readonly ActivityContext activityContext = activityContext;
        private readonly Baggage baggage = baggage;

        public override ISet<string> Fields => throw new NotImplementedException();

        public override PropagationContext Extract<T>(PropagationContext context, T carrier, Func<T, string, IEnumerable<string>?> getter)
        {
            return new PropagationContext(this.activityContext, this.baggage);
        }

        public override void Inject<T>(PropagationContext context, T carrier, Action<T, string, string> setter)
        {
            throw new NotImplementedException();
        }
    }

    private class TestSampler(SamplingDecision samplingDecision, IEnumerable<KeyValuePair<string, object>>? attributes = null) : Sampler
    {
        private readonly SamplingDecision samplingDecision = samplingDecision;
        private readonly IEnumerable<KeyValuePair<string, object>>? attributes = attributes;

        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
        {
            return new SamplingResult(this.samplingDecision, this.attributes);
        }
    }

    private class TestHttpInListener(AspNetCoreTraceInstrumentationOptions options) : HttpInListener(options)
    {
        public Action<string, object?>? OnEventWrittenCallback;

        public override void OnEventWritten(string name, object? payload)
        {
            base.OnEventWritten(name, payload);

            this.OnEventWrittenCallback?.Invoke(name, payload);
        }
    }

    private class TestNullHostActivityMiddlewareImpl(string activitySourceName, string activityName) : TestActivityMiddleware
    {
        private readonly ActivitySource activitySource = new(activitySourceName);
        private readonly string activityName = activityName;
        private Activity? activity;

        public override void PreProcess(HttpContext context)
        {
            // Setting the host activity i.e. activity started by asp.net core
            // to null here will have no impact on middleware activity.
            // This also means that asp.net core activity will not be found
            // during OnEventWritten event.
            Activity.Current = null;
            this.activity = this.activitySource.StartActivity(this.activityName);
        }

        public override void PostProcess(HttpContext context)
        {
            this.activity?.Stop();
        }
    }

    private class TestTestActivityMiddleware(string activitySourceName, string activityName) : TestActivityMiddleware
    {
        private readonly ActivitySource activitySource = new(activitySourceName);
        private readonly string activityName = activityName;
        private Activity? activity;

        public override void PreProcess(HttpContext context)
        {
            this.activity = this.activitySource.StartActivity(this.activityName);
        }

        public override void PostProcess(HttpContext context)
        {
            this.activity?.Stop();
        }
    }
}
