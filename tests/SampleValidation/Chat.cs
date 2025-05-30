// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace SampleValidation;

public class Chat
{
    readonly ITestOutputHelper output;

    public Chat(ITestOutputHelper output)
    {
        this.output = output;
    }

    // IMPORTANT: This test assumes that the Functions host is running the sample app in a separate process.
    //            The address of the Functions host is read from the FUNC_BASE_ADDRESS environment variable.
    [Fact]
    public async Task SuperbowlQuestions()
    {
        using HttpClient client = new(new LoggingHandler(this.output));
        using CancellationTokenSource cts = new(delay: TimeSpan.FromMinutes(Debugger.IsAttached ? 5 : 1));

        string baseAddress = Environment.GetEnvironmentVariable("FUNC_BASE_ADDRESS") ?? "http://localhost:7071";
        string chatId = $"superbowl-{Guid.NewGuid():N}";

#if RELEASE
        // Use the default key for the Azure Functions app in RELEASE mode; for local development, DEBUG mode can be used.
        string functionKey = Environment.GetEnvironmentVariable("FUNC_DEFAULT_KEY") ?? throw new InvalidOperationException("Missing environment variable 'FUNC_DEFAULT_KEY'");
        client.DefaultRequestHeaders.Add("x-functions-key", functionKey);
#endif

        // The timestamp is used for message filtering and will be updated by the ValidateAssistantResponseAsync function
        DateTime timestamp = DateTime.UtcNow;

        // Create a new assistant using an HTTP PUT request
        var createRequest = new { instructions = "You are a helpful assistant. Prefix each response with 'Yo!'." };
        using HttpResponseMessage createResponse = await client.PutAsJsonAsync(
            requestUri: $"{baseAddress}/api/chats/{chatId}",
            createRequest,
            cancellationToken: cts.Token);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.StartsWith("application/json", createResponse.Content.Headers.ContentType?.MediaType);

        // Expected: {"chatId":"superbowl-<random>"}
        string createResponseContent = await createResponse.Content.ReadAsStringAsync(cts.Token);
        JsonNode? createResponseJson = JsonNode.Parse(createResponseContent);
        Assert.NotNull(createResponseJson);
        Assert.Equal(chatId, createResponseJson!["chatId"]?.GetValue<string>());

        // Wait for the chat bot to be initialized
        await ValidateAssistantResponseAsync(expectedMessageCount: 1, expectedContent: createRequest.instructions);

        // Ask a question using an HTTP POST request
        string questionRequest1 = "Who won the Superbowl in 2014?";
        using HttpResponseMessage questionResponse = await client.PostAsync(
            requestUri: $"{baseAddress}/api/chats/{chatId}?message={questionRequest1}", null,
            cancellationToken: cts.Token);
        Assert.Equal(HttpStatusCode.OK, questionResponse.StatusCode);
        Assert.StartsWith("text/plain", questionResponse.Content.Headers.ContentType?.MediaType);

        // Ensure that the model responded and mentioned the Seahawks as the 2014 Superbowl winners.
        await ValidateAssistantResponseAsync(expectedMessageCount: 3, expectedContent: "Seahawks", hasTotalTokens: true);

        string questionRequest2 = "Who performed the halftime show?";

        using HttpResponseMessage followupResponse = await client.PostAsync(
            requestUri: $"{baseAddress}/api/chats/{chatId}?message={questionRequest2}", null,
            cancellationToken: cts.Token);
        Assert.Equal(HttpStatusCode.OK, questionResponse.StatusCode);
        Assert.StartsWith("text/plain", questionResponse.Content.Headers.ContentType?.MediaType);

        // Ensure that the model responded with Bruno Mars as the halftime show performer.
        await ValidateAssistantResponseAsync(expectedMessageCount: 5, expectedContent: "Bruno Mars", hasTotalTokens: true);

        // Local function to validate each chat bot response
        async Task ValidateAssistantResponseAsync(int expectedMessageCount, string expectedContent, bool hasTotalTokens = false)
        {
            // It may take a few seconds for the chat bot response to appear
            while (!cts.IsCancellationRequested)
            {
                using HttpResponseMessage stateResponse = await client.GetAsync(
                    requestUri: $"{baseAddress}/api/chats/{chatId}?timestampUTC={Uri.EscapeDataString(timestamp.ToString("o"))}");
                Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);
                Assert.StartsWith("application/json", stateResponse.Content.Headers.ContentType?.MediaType);

                string responseContent = await stateResponse.Content.ReadAsStringAsync(cts.Token);
                JsonNode? json = JsonNode.Parse(responseContent);
                Assert.NotNull(json);
                Assert.Equal(chatId, json!["id"]?.GetValue<string>());
                int totalMessages = json!["totalMessages"]?.GetValue<int>() ?? -1;
                Assert.True(totalMessages >= 0, "Field 'totalMessages' is missing");

                if (totalMessages > 0)
                {
                    Assert.True(json!["exists"]?.GetValue<bool>());
                }

                if (hasTotalTokens)
                {
                    int totalTokens = json!["totalTokens"]?.GetValue<int>() ?? -1;
                    Assert.True(totalTokens > 0, "Field 'totalTokens' is not set.");
                }

                JsonArray? messageArray = json!["recentMessages"]?.AsArray();
                Assert.NotNull(messageArray);

                // The timestamp filter should ensure we only ever look at the most recent messages
                Assert.True(messageArray!.Count <= totalMessages);
                Assert.True(messageArray!.Count <= 2);

                if (totalMessages >= expectedMessageCount)
                {
                    // Make sure we don't have more messages than expected
                    Assert.Equal(expectedMessageCount, totalMessages);

                    if (totalMessages == 1)
                    {
                        // Make sure the first message is the system message
                        JsonNode systemMessage = messageArray!.First()!;
                        Assert.Equal("system", systemMessage["role"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        // Make sure that the last message is from the chat bot (assistant)
                        JsonNode lastMessage = messageArray![messageArray.Count - 1]!;
                        Assert.Equal("assistant", lastMessage["role"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
                        Assert.StartsWith("Yo!", lastMessage!["content"]?.GetValue<string>());
                    }

                    Assert.Contains(expectedContent, messageArray!.Last()!["content"]?.GetValue<string>());

                    // Update the timestamp so that the next fetch only gets the unread messages
                    timestamp = DateTime.Parse(json["lastUpdatedAt"]!.GetValue<string>(), null, DateTimeStyles.RoundtripKind);
                    break;
                }

                // Response hasn't come yet - wait a little longer. Expected delays include queue polling
                // delays and latency with the OpenAI APIs.
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
        }
    }    
}