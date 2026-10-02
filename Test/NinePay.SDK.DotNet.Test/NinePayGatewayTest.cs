using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using NinePay.SDK.DotNet.Config;
using NinePay.SDK.DotNet.Gateway;
using NinePay.SDK.DotNet.Request;
using NinePay.SDK.DotNet.Response;

namespace NinePay.SDK.DotNet.Test
{
    public class NinePayGatewayTest
    {
        private readonly NinePayGateway gateway;

        public NinePayGatewayTest()
        {
            var endpoint = System.Environment.GetEnvironmentVariable("NINEPAY_ENDPOINT") ?? "https://sandbox.example.com";
            var config = new NinePayConfig("MID", "SECRET", "CHECKSUM", endpoint);
            gateway = new NinePayGateway(config);
        }

        [Fact]
        public void TestCreatePayment()
        {
            var request = new CreatePaymentRequest("INV123", "1000", "Test");
            ResponseInterface response = gateway.CreatePayment(request);

            Assert.True(response.IsSuccess());
            Assert.NotNull(response.GetData()["redirect_url"]);

            var redirectUrl = response.GetData()["redirect_url"].ToString();
            Assert.Contains("baseEncode=", redirectUrl);
            Assert.Contains("signature=", redirectUrl);
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        [InlineData("\"true\"", true)]
        [InlineData("1", false)]
        public async System.Threading.Tasks.Task RefundReadsSuccessFromJson(string successJson, bool expectedSuccess)
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();

            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            var serverTask = System.Threading.Tasks.Task.Run(async () =>
            {
                var context = await listener.GetContextAsync();
                var body = Encoding.UTF8.GetBytes($"{{\"success\":{successJson},\"message\":\"OK\"}}");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            });

            var localGateway = new NinePayGateway(
                new NinePayConfig("MID", "SECRET", "CHECKSUM", $"http://127.0.0.1:{port}"));
            var response = localGateway.Refund(new ReverseRequest("REFUND_123", "INV123"));
            await serverTask;

            Assert.Equal(expectedSuccess, response.IsSuccess());
            Assert.Equal("OK", response.GetMessage());
        }
    }
}
