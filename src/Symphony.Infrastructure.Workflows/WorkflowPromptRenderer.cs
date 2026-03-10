using Scriban;
using Scriban.Runtime;
using Symphony.Core.Models;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Infrastructure.Workflows;

public sealed class WorkflowPromptRenderer : IWorkflowPromptRenderer
{
    public string RenderForIssue(WorkflowDefinition workflowDefinition, NormalizedIssue issue, int? attempt = null)
    {
        var templateText = workflowDefinition.PromptTemplate;
        if (string.IsNullOrWhiteSpace(templateText))
        {
            return "You are working on a GitHub issue.";
        }

        var template = Template.Parse(templateText);
        if (template.HasErrors)
        {
            var message = string.Join("; ", template.Messages.Select(m => m.ToString()));
            throw new WorkflowLoadException("template_parse_error", message);
        }

        var pullRequests = issue.PullRequests
            .Select(pr =>
            {
                var pullRequest = new ScriptObject();
                pullRequest.SetValue("id", pr.Id, true);
                pullRequest.SetValue("number", pr.Number, true);
                pullRequest.SetValue("state", pr.State, true);
                pullRequest.SetValue("url", pr.Url, true);
                pullRequest.SetValue("head_ref", pr.HeadRef, true);
                pullRequest.SetValue("base_ref", pr.BaseRef, true);
                return pullRequest;
            })
            .ToList();

        var blockers = issue.BlockedBy
            .Select(blocker =>
            {
                var blockerModel = new ScriptObject();
                blockerModel.SetValue("id", blocker.Id, true);
                blockerModel.SetValue("identifier", blocker.Identifier, true);
                blockerModel.SetValue("state", blocker.State, true);
                return blockerModel;
            })
            .ToList();

        var issueModel = new ScriptObject();
        issueModel.SetValue("id", issue.Id, true);
        issueModel.SetValue("identifier", issue.Identifier, true);
        issueModel.SetValue("title", issue.Title, true);
        issueModel.SetValue("description", issue.Description, true);
        issueModel.SetValue("priority", issue.Priority, true);
        issueModel.SetValue("state", issue.State, true);
        issueModel.SetValue("branch_name", issue.BranchName, true);
        issueModel.SetValue("url", issue.Url, true);
        issueModel.SetValue("milestone", issue.Milestone, true);
        issueModel.SetValue("labels", issue.Labels.ToList(), true);
        issueModel.SetValue("pull_requests", pullRequests, true);
        issueModel.SetValue("blocked_by", blockers, true);
        issueModel.SetValue("created_at", issue.CreatedAt, true);
        issueModel.SetValue("updated_at", issue.UpdatedAt, true);

        var model = new ScriptObject();
        model.SetValue("issue", issueModel, true);
        model.SetValue("attempt", attempt, true);

        var context = new TemplateContext
        {
            StrictVariables = true,
            EnableRelaxedFunctionAccess = false,
            EnableRelaxedIndexerAccess = false,
            EnableRelaxedMemberAccess = false,
            EnableRelaxedTargetAccess = false
        };
        context.PushGlobal(model);

        try
        {
            return template.Render(context).Trim();
        }
        catch (Exception ex)
        {
            throw new WorkflowLoadException("template_render_error", ex.Message, ex);
        }
    }
}
