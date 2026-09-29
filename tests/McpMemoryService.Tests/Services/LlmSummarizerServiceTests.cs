namespace McpMemoryService.Tests.Services;

using McpMemoryService.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

/// <summary>
/// [PURPOSE]: Unit tests for LlmSummarizerService — mocked HttpClient, no real llama.cpp.
/// </summary>
public class LlmSummarizerServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactory;
    private readonly LlmSummarizerService _service;
    private readonly NullLogger<LlmSummarizerService> _logger;

    public LlmSummarizerServiceTests()
    {
        _logger = new NullLogger<LlmSummarizerService>();
        _httpClientFactory = new Mock<IHttpClientFactory>();
        _service = new LlmSummarizerService(_httpClientFactory.Object, _logger);
    }

    [Fact]
    public async Task SummarizeAsync_ReturnsContent_OnSuccess()
    {
        var handler = new FakeHttpMessageHandler(
            statusCode: System.Net.HttpStatusCode.OK,
            content: "{\"content\":\"summary text\",\"tokens_evaluated\":5}");
        var client = new HttpClient(handler) { BaseAddress = new System.Uri("http://test/") };

        _httpClientFactory.Setup(f => f.CreateClient(It.Is<string>(n => n == "LlamaCpp")))
            .Returns(client);

        var result = await _service.SummarizeAsync(new List<string> { "entry A" });

        Assert.Equal("summary text", result);
    }

    [Fact]
    public async Task SummarizeAsync_BuildsPromptWithAllContents()
    {
        var handler = new CapturingHttpMessageHandler();
        var client = new HttpClient(handler) { BaseAddress = new System.Uri("http://test/") };

        _httpClientFactory.Setup(f => f.CreateClient(It.Is<string>(n => n == "LlamaCpp")))
            .Returns(client);

        await _service.SummarizeAsync(new List<string> { "entry A", "entry B" });

        var capturedBody = handler.CapturedBody;
        Assert.NotNull(capturedBody);
        Assert.Contains("entry A", capturedBody);
        Assert.Contains("entry B", capturedBody);
    }

    [Fact]
    public async Task SummarizeAsync_ThrowsOn5xx()
    {
        var handler = new FakeHttpMessageHandler(statusCode: System.Net.HttpStatusCode.InternalServerError);
        var client = new HttpClient(handler) { BaseAddress = new System.Uri("http://test/") };

        _httpClientFactory.Setup(f => f.CreateClient(It.Is<string>(n => n == "LlamaCpp")))
            .Returns(client);

        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => _service.SummarizeAsync(new List<string> { "entry" }));
    }

    [Fact]
    public async Task SummarizeAsync_ThrowsTaskCanceled_OnTimeout()
    {
        var handler = new DelayedHttpMessageHandler(TimeSpan.FromSeconds(2));
        var client = new HttpClient(handler) { BaseAddress = new System.Uri("http://test/"), Timeout = TimeSpan.FromMilliseconds(100) };

        _httpClientFactory.Setup(f => f.CreateClient(It.Is<string>(n => n == "LlamaCpp")))
            .Returns(client);

        await Assert.ThrowsAsync<System.Threading.Tasks.TaskCanceledException>(() => _service.SummarizeAsync(new List<string> { "entry" }));
    }

    [Fact]
    public async Task SummarizeAsync_EmptyContents_ThrowsArgumentException()
    {
        var ex = await Assert.ThrowsAsync<System.ArgumentException>(() => _service.SummarizeAsync(new List<string>()));
        Assert.Contains("contents", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SummarizeAsync_NullContents_ThrowsArgumentNullException()
    {
        var ex = await Assert.ThrowsAsync<System.ArgumentNullException>(() => _service.SummarizeAsync(null!));
        Assert.Contains("contents", ex.ParamName, StringComparison.OrdinalIgnoreCase);
    }

    #region Test Helpers

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _statusCode;
        private readonly string _content;

        public FakeHttpMessageHandler(System.Net.HttpStatusCode statusCode, string content = "")
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content)
            });
        }
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"content\":\"ok\"}")
            };
        }
    }

    private sealed class DelayedHttpMessageHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public DelayedHttpMessageHandler(TimeSpan delay)
        {
            _delay = delay;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.Delay(_delay, cancellationToken).ContinueWith(
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                },
                cancellationToken,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    #endregion Test Helpers
}
