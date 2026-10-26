using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#if !NETFRAMEWORK
using System.Net.Http.Headers;
#endif

namespace SwyfCloudTts.Providers;

/// One HTTP request. Providers describe requests with this instead of HttpRequestMessage
/// because the game's System.Net.Http is stripped: inside the game the request goes out
/// through HttpWebRequest, in the worker through HttpClient.
internal sealed class HttpCall
{
    public HttpCall(string method, string url)
    {
        Method = method;
        Url = url;
    }

    public string Method { get; }

    public string Url { get; }

    public List<KeyValuePair<string, string>> Headers { get; } = new();

    public byte[]? Body { get; private set; }

    public string? ContentType { get; private set; }

    public static HttpCall Get(string url) => new("GET", url);

    public static HttpCall Delete(string url) => new("DELETE", url);

    public static HttpCall PostJson(string url, object payload)
    {
        var call = new HttpCall("POST", url);
        call.SetBody(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)), "application/json; charset=utf-8");
        return call;
    }

    public static HttpCall PostForm(string url, MultipartForm form)
    {
        var call = new HttpCall("POST", url);
        call.SetBody(form.ToArray(), form.ContentType);
        return call;
    }

    public HttpCall Header(string name, string value)
    {
        Headers.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    public HttpCall Bearer(string key) => Header("Authorization", "Bearer " + key);

    private void SetBody(byte[] body, string contentType)
    {
        Body = body;
        ContentType = contentType;
    }
}

/// multipart/form-data body, built by hand so both transports send the same bytes.
internal sealed class MultipartForm
{
    private readonly string _boundary = "swyf-" + Guid.NewGuid().ToString("N");
    private readonly MemoryStream _body = new();

    public string ContentType => "multipart/form-data; boundary=" + _boundary;

    public MultipartForm Text(string name, string value)
    {
        Write($"--{_boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n" +
              "Content-Type: text/plain; charset=utf-8\r\n\r\n");
        Write(value);
        Write("\r\n");
        return this;
    }

    public MultipartForm File(string name, string fileName, string contentType, byte[] data)
    {
        Write($"--{_boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{fileName}\"\r\n" +
              $"Content-Type: {contentType}\r\n\r\n");
        _body.Write(data, 0, data.Length);
        Write("\r\n");
        return this;
    }

    public byte[] ToArray()
    {
        var copy = new MemoryStream();
        _body.Position = 0;
        _body.CopyTo(copy);
        byte[] closing = Encoding.UTF8.GetBytes($"--{_boundary}--\r\n");
        copy.Write(closing, 0, closing.Length);
        return copy.ToArray();
    }

    private void Write(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _body.Write(bytes, 0, bytes.Length);
    }
}

/// A response whose body is still open for streaming.
internal sealed class HttpReply : IDisposable
{
    private readonly IDisposable? _owner;

    public HttpReply(HttpStatusCode status, Stream body, IDisposable? owner)
    {
        Status = status;
        Body = body;
        _owner = owner;
    }

    public HttpStatusCode Status { get; }

    public Stream Body { get; }

    public bool Success => (int)Status >= 200 && (int)Status <= 299;

    public async Task<byte[]> ReadBytesAsync(CancellationToken token)
    {
        var copy = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = await Body.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
        {
            copy.Write(buffer, 0, read);
        }

        return copy.ToArray();
    }

    public async Task<string> ReadTextAsync(CancellationToken token)
        => Encoding.UTF8.GetString(await ReadBytesAsync(token).ConfigureAwait(false));

    public async Task<JObject> ReadJsonAsync(CancellationToken token)
    {
        string text = await ReadTextAsync(token).ConfigureAwait(false);
        return Config.ParseJson(text) as JObject ?? throw new InvalidDataException("expected a JSON object");
    }

    public void Dispose()
    {
        try
        {
            Body.Dispose();
        }
        catch
        {
            // The connection may already be gone.
        }

        _owner?.Dispose();
    }
}

/// Status codes .NET Framework's enum does not name.
internal static class HttpStatus
{
    public const HttpStatusCode UnprocessableEntity = (HttpStatusCode)422;
    public const HttpStatusCode TooManyRequests = (HttpStatusCode)429;
}

/// An HTTP failure. StatusCode is null when no response arrived at all (DNS, refused, TLS).
internal sealed class HttpFailure : Exception
{
    public HttpFailure(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

/// Sends <see cref="HttpCall"/>s. Returns every response, successful or not; only a missing
/// response is an exception.
internal static class HttpTransport
{
#if NETFRAMEWORK
    static HttpTransport()
    {
        // Several lines are requested back to back; keep their connections open.
        ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 16);
        ServicePointManager.Expect100Continue = false;
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    }

    public static async Task<HttpReply> SendAsync(HttpCall call, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = (HttpWebRequest)WebRequest.Create(call.Url);
        request.Method = call.Method;
        request.Timeout = Timeout.Infinite;
        request.ReadWriteTimeout = Timeout.Infinite;
        request.KeepAlive = true;
        request.AllowReadStreamBuffering = false;
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

        foreach (KeyValuePair<string, string> header in call.Headers)
        {
            request.Headers[header.Key] = header.Value;
        }

        // Mono's response stream ignores cancellation tokens, so cancelling aborts the request,
        // for as long as the body is being read.
        CancellationTokenRegistration abort = token.Register(request.Abort);
        try
        {
            if (call.Body != null)
            {
                request.ContentType = call.ContentType;
                request.ContentLength = call.Body.Length;
                using Stream upload = await request.GetRequestStreamAsync().ConfigureAwait(false);
                await upload.WriteAsync(call.Body, 0, call.Body.Length, token).ConfigureAwait(false);
            }

            WebResponse response;
            try
            {
                response = await request.GetResponseAsync().ConfigureAwait(false);
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse failed)
            {
                response = failed;
            }

            var http = (HttpWebResponse)response;
            return new HttpReply(http.StatusCode, http.GetResponseStream() ?? Stream.Null, new Owner(http, abort));
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is not OperationCanceledException)
        {
            abort.Dispose();
            throw new OperationCanceledException(token);
        }
        catch (WebException ex)
        {
            abort.Dispose();
            throw new HttpFailure(ex.Message, null, ex);
        }
        catch
        {
            abort.Dispose();
            throw;
        }
    }

    private sealed class Owner : IDisposable
    {
        private readonly HttpWebResponse _response;
        private readonly CancellationTokenRegistration _abort;

        public Owner(HttpWebResponse response, CancellationTokenRegistration abort)
        {
            _response = response;
            _abort = abort;
        }

        public void Dispose()
        {
            _abort.Dispose();
            ((IDisposable)_response).Dispose();
        }
    }
#else
    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestVersion = HttpVersion.Version11,
    };

    public static async Task<HttpReply> SendAsync(HttpCall call, CancellationToken token)
    {
        var request = new HttpRequestMessage(new HttpMethod(call.Method), call.Url);
        foreach (KeyValuePair<string, string> header in call.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (call.Body != null)
        {
            request.Content = new ByteArrayContent(call.Body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(call.ContentType!);
        }

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            request.Dispose();
            throw new HttpFailure(ex.Message, null, ex);
        }

        Stream body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return new HttpReply(response.StatusCode, body, new Owner(request, response));
    }

    private sealed class Owner : IDisposable
    {
        private readonly HttpRequestMessage _request;
        private readonly HttpResponseMessage _response;

        public Owner(HttpRequestMessage request, HttpResponseMessage response)
        {
            _request = request;
            _response = response;
        }

        public void Dispose()
        {
            _response.Dispose();
            _request.Dispose();
        }
    }
#endif
}
