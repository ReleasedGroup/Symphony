using Symphony.Core.Models;

namespace Symphony.Infrastructure.Workflows;

public interface IWorkflowPromptRenderer
{
    string RenderForIssue(
        Models.WorkflowDefinition workflowDefinition,
        NormalizedIssue issue,
        int? attempt = null);
}
