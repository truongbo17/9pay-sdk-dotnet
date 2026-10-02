using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NinePay.SDK.DotNet.Config;
using NinePay.SDK.DotNet.Request;
using NinePay.SDK.DotNet.Response;
using NinePay.SDK.DotNet.Utils;

namespace NinePay.SDK.DotNet.Gateway
{
    public class NinePayGateway : PaymentGatewayInterface
    {
        private readonly string clientId;
        private readonly string secretKey;
        private readonly string checksumKey;
        private readonly string endpoint;
        private readonly HttpClient httpClient;

        public NinePayGateway(NinePayConfig config)
        {
            clientId = config.MerchantId;
            secretKey = config.SecretKey;
            checksumKey = config.ChecksumKey;

            endpoint = config.Endpoint;

            httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
        }

        // ========== createPayment ==========
        public ResponseInterface CreatePayment(CreatePaymentRequest paymentRequest)
        {
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            var payload = new Dictionary<string, object>
            {
                ["merchantKey"] = clientId,
                ["time"] = time
            };

            foreach (var kv in paymentRequest.ToPayload())
            {
                payload[kv.Key] = kv.Value;
            }

            // convert payload to string map (null-safe)
            var stringParams = payload
                .Where(x => x.Value != null)
                .ToDictionary(
                    k => k.Key,
                    v => v.Value!.ToString()!
                );

            var message = MessageBuilder.Instance()
                .With(time, endpoint + "/payments/create", "POST")
                .WithParams(stringParams)
                .Build();

            var signature = Signature.Sign(message, secretKey);

            var jsonPayload = JsonSerializer.Serialize(payload);
            var baseEncode = Convert.ToBase64String(Encoding.UTF8.GetBytes(jsonPayload));

            var redirectUrl = endpoint + "/portal?"
                + "baseEncode=" + Uri.EscapeDataString(baseEncode)
                + "&signature=" + Uri.EscapeDataString(signature);

            var data = new Dictionary<string, object>
            {
                ["redirect_url"] = redirectUrl
            };

            return new PaymentResponse(true, data, "OK");
        }

        // ========== inquiry ==========
        public ResponseInterface Inquiry(string transactionId)
        {
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var uri = endpoint + "/v2/payments/" + transactionId + "/inquire";

            var message = MessageBuilder.Instance()
                .With(time, uri, "GET")
                .Build();

            var signature = Signature.Sign(message, secretKey);

            var authHeader =
                "Signature Algorithm=HS256,Credential=" + clientId +
                ",SignedHeaders=,Signature=" + signature;

            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("Date", time);
            request.Headers.Add("Authorization", authHeader);

            var response = httpClient.Send(request);
            var body = response.Content.ReadAsStringAsync().Result;

            var data = JsonSerializer.Deserialize<Dictionary<string, object>>(body)
                       ?? new Dictionary<string, object>();

            var messageStr = data.ContainsKey("message")
                ? data["message"]?.ToString() ?? ""
                : "";

            return new PaymentResponse(response.IsSuccessStatusCode, data, messageStr);
        }

        // ========== verify ==========
        public bool Verify(string result, string checksum)
        {
            if (string.IsNullOrEmpty(result) || string.IsNullOrEmpty(checksum))
                return false;

            var combined = result + checksumKey;

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));

            var hex = BitConverter.ToString(hash).Replace("-", "").ToLower();

            return hex.Equals(checksum, StringComparison.OrdinalIgnoreCase);
        }

        // ========== decodeResult ==========
        public string DecodeResult(string result)
        {
            if (string.IsNullOrEmpty(result)) return "";

            try
            {
                var normalized = result.Replace('-', '+').Replace('_', '/');
                var bytes = Convert.FromBase64String(normalized);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                try
                {
                    var bytes = Convert.FromBase64String(result);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    return "";
                }
            }
        }

        // ========== refund ==========
        public PaymentResponse Refund(ReverseRequest request)
        {
            var url = endpoint + "/api/v2/refund";

            var payload = request.ToPayload();
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            string httpQuery = SignatureV2Util.BuildHttpQuery(payload);

            string signature = SignatureV2Util.CreateSignature(
                "POST",
                "/api/v2/refund",
                timestamp,
                httpQuery,
                secretKey
            );

            var headers = new Dictionary<string, string>
            {
                { "X-Request-Time", timestamp.ToString() },
                { "X-Signature", signature },
                { "Content-Type", "application/x-www-form-urlencoded" }
            };

            string response = HttpHelper.PostForm(url, payload, headers);

            // ⚠️ Nếu API trả HTML -> coi như lỗi
            if (string.IsNullOrEmpty(response) || response.TrimStart().StartsWith("<"))
            {
                return new PaymentResponse(false, null, "Invalid response from refund API");
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, object>>(response)
                       ?? new Dictionary<string, object>();

            bool success = data.TryGetValue("success", out var successValue)
                && successValue is JsonElement successElement
                && (successElement.ValueKind == JsonValueKind.True
                    || (successElement.ValueKind == JsonValueKind.String && successElement.GetString() == "true"));

            string message = data.ContainsKey("message")
                ? data["message"]?.ToString() ?? ""
                : "";

            return new PaymentResponse(success, data, message);
        }

    }
}
