// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenAI.Chat;

namespace Microsoft.Azure.WebJobs.Extensions.OpenAI.Assistants;

public interface IAssistantSkillInvoker
{
    IList<ChatTool>? GetFunctionsDefinitions();
    Task<string?> InvokeAsync(ChatToolCall call, CancellationToken cancellationToken);
}

class SkillInvocationContext
{
    public SkillInvocationContext(BinaryData arguments)
    {
        this.Arguments = arguments;
    }

    // The arguments are passed as a JSON object in the form of {"paramName":paramValue}
    public BinaryData Arguments { get; }

    // The result of the function invocation, if any
    public object? Result { get; set; }
}

public class AssistantSkillManager : IAssistantSkillInvoker
{
    record Skill(
        string Name,
        AssistantSkillTriggerAttribute Attribute,
        ParameterInfo Parameter,
        ITriggeredFunctionExecutor Executor);

    readonly ILogger logger;

    readonly Dictionary<string, Skill> skills = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="AssistantSkillManager"/> class.
    /// </summary>
    /// <remarks>
    /// This constructor is called by the dependency management container in the Functions (WebJobs) runtime.
    /// </remarks>
    public AssistantSkillManager(ILoggerFactory loggerFactory)
    {
        this.logger = loggerFactory.CreateLogger<AssistantSkillManager>();
    }

    internal void RegisterSkill(
        string name,
        AssistantSkillTriggerAttribute attribute,
        ParameterInfo parameter,
        ITriggeredFunctionExecutor executor)
    {
        this.logger.LogInformation("Registering skill '{Name}'", name);
        this.skills.Add(name, new Skill(name, attribute, parameter, executor));
    }

    internal void UnregisterSkill(string name)
    {
        this.logger.LogInformation("Unregistering skill '{Name}'", name);
        this.skills.Remove(name);
    }

    IList<ChatTool>? IAssistantSkillInvoker.GetFunctionsDefinitions()
    {
        if (this.skills.Count == 0)
        {
            return null;
        }

        List<ChatTool> functions = new(capacity: this.skills.Count);
        foreach (Skill skill in this.skills.Values)
        {
            // The parameters can be defined in the attribute JSON or can be inferred from
            // the .NET (in-proc) function signature, if applicable.
            string parametersJson = skill.Attribute.ParameterDescriptionJson ??
                JsonConvert.SerializeObject(GetParameterDefinition(skill));

            ChatTool chatTool = ChatTool.CreateFunctionTool(
                skill.Name,
                skill.Attribute.FunctionDescription,
                BinaryData.FromBytes(Encoding.UTF8.GetBytes(parametersJson))
                );
            functions.Add(chatTool);
        }

        return functions;
    }

    static Dictionary<string, object> GetParameterDefinition(Skill skill)
    {
        // Try to infer from the .NET parameter type (only works with in-proc WebJobs)
        string type;
        switch (skill.Parameter.ParameterType)
        {
            case Type t when t == typeof(string):
                type = "string";
                break;
            case Type t when t == typeof(int):
                type = "integer";
                break;
            case Type t when t == typeof(bool):
                type = "boolean";
                break;
            case Type t when t == typeof(float):
                type = "number";
                break;
            case Type t when t == typeof(double):
                type = "number";
                break;
            case Type t when t == typeof(decimal):
                type = "number";
                break;
            case Type _ when typeof(System.Collections.IEnumerable).IsAssignableFrom(skill.Parameter.ParameterType):
                type = "array";
                break;
            default:
                type = "string";
                break;
        }

        // Schema reference: https://platform.openai.com/docs/api-reference/chat/create#chat-create-tools
        return new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                [skill.Parameter.Name] = new { type }
            }
        };
    }

    async Task<string?> IAssistantSkillInvoker.InvokeAsync(
        ChatToolCall call,
        CancellationToken cancellationToken)
    {
        if (call is null)
        {
            throw new ArgumentNullException(nameof(call));
        }

        if (call.FunctionName is null)
        {
            throw new ArgumentException("The function call must have a name", nameof(call));
        }

        if (!this.skills.TryGetValue(call.FunctionName, out Skill? skill))
        {
            throw new InvalidOperationException($"No skill registered with name '{call.FunctionName}'");
        }

        SkillInvocationContext skillInvocationContext = new(call.FunctionArguments);

        // This call may throw if the Functions host is shutting down or if there is an internal error
        // in the Functions runtime. We don't currently try to handle these exceptions.
        FunctionResult result = await skill.Executor.TryExecuteAsync(
            new TriggeredFunctionData
            {
                TriggerValue = skillInvocationContext,
#pragma warning disable CS0618 // Approved for use by this extension
                InvokeHandler = async userCodeInvoker =>
                {
                    // Invoke the function and attempt to get the result.
                    this.logger.LogInformation("Invoking user-code function '{Name}'", call.FunctionName);
                    Task invokeTask = userCodeInvoker.Invoke();
                    if (invokeTask is Task<object> invokeTaskWithResult)
                    {
                        skillInvocationContext.Result = await invokeTaskWithResult;
                    }
                    else
                    {
                        // This is generally not expected to happen, but we handle it just in case.
                        this.logger.LogWarning(
                            "Unable to discover the return value (if any) for user-code function '{Name}'. " +
                            "This is an internal error in the extension that may result in model hallucination.",
                            call.FunctionName);
                        await invokeTask;
                    }
                }
#pragma warning restore CS0618
            },
            cancellationToken);

        // If the function threw an exception, rethrow it here. This will cause the caller (e.g., the
        // assistant service) to receive an error response, which it should be prepared to catch and handle.
        if (result.Exception is not null)
        {
            ExceptionDispatchInfo.Throw(result.Exception);
        }

        if (skillInvocationContext.Result is null)
        {
            return null;
        }

        // Convert the output to JSON
        string jsonResult = JsonConvert.SerializeObject(skillInvocationContext.Result);
        this.logger.LogInformation(
            "Returning output of user-code function '{Name}' as JSON: {Json}", call.FunctionName, jsonResult);
        return jsonResult;
    }
}