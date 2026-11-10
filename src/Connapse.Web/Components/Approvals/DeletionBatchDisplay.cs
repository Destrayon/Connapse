using Connapse.Application.Approvals;

namespace Connapse.Web.Components.Approvals;

/// <summary>How a batch of deletions (CON-315) shows on the approvals page: each item's link, kind and outcome badge.</summary>
public static class DeletionBatchDisplay
{
    /// <summary>Where the owner checks the item before deciding: the file's details, the folder, or the memory space.</summary>
    public static string Link(DeletionBatchItemView item) => item.Kind switch
    {
        "file" => $"containers/{item.ContainerId}?file={Uri.EscapeDataString(item.Target)}",
        "folder" => $"containers/{item.ContainerId}?path={Uri.EscapeDataString(item.Target)}",
        _ => $"memory/{item.ContainerId}",
    };

    /// <summary>The folder a <c>?path=</c> link opens, as the file browser writes folders: "/" or "/a/b/".</summary>
    public static string StartFolder(string? path)
    {
        string trimmed = (path ?? "").Trim().Trim('/');
        return trimmed.Length == 0 ? "/" : $"/{trimmed}/";
    }

    public static string KindLabel(string kind) => kind switch
    {
        "file" => "File",
        "folder" => "Folder",
        _ => "Memory space",
    };

    public static string KindIcon(string kind) => kind switch
    {
        "file" => "bi-file-earmark",
        "folder" => "bi-folder",
        _ => "bi-journal-text",
    };

    public static string StateBadge(string state) => state switch
    {
        "deleted" => "text-bg-success",
        "skipped" => "text-bg-warning",
        "failed" => "text-bg-danger",
        _ => "text-bg-secondary",
    };

    public static string DecidedMessage(DeletionBatchOutcome status, bool approve) => status switch
    {
        DeletionBatchOutcome.Success when approve => "Approved. Connapse deleted the approved items; the agent can read each outcome.",
        DeletionBatchOutcome.Success => "Denied. Nothing was deleted, and the agent is told why.",
        DeletionBatchOutcome.NotPending => "That batch was already decided or has expired.",
        DeletionBatchOutcome.NothingSelected => "Select at least one item to approve, or deny the batch.",
        _ => "That batch was not found.",
    };
}
