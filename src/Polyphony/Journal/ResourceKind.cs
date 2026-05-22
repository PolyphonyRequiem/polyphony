namespace Polyphony.Journal;

public static class ResourceKind
{
    public const string GitBranch = "git_branch";
    public const string GitTag = "git_tag";
    public const string GitWorktree = "git_worktree";
    public const string GitHubPr = "github_pr";
    public const string GitHubPrComment = "github_pr_comment";
    public const string AdoPr = "ado_pr";
    public const string AdoPrComment = "ado_pr_comment";
    public const string AdoPrVote = "ado_pr_vote";
    public const string AdoWorkItem = "ado_work_item";
    public const string AdoWorkItemTag = "ado_work_item_tag";
    public const string AdoWorkItemState = "ado_work_item_state";
    public const string ManifestFile = "manifest_file";
    public const string PlanFile = "plan_file";
}
