namespace Firepit.Core.Updates;

/// <summary>
/// The result of asking whether a newer Firepit exists — including the case
/// where the question could not be asked.
/// </summary>
/// <remarks>
/// The caption-bar badge only appears on good news, which makes "you are up to
/// date" and "the check has been failing for weeks" look identical. Anything
/// that reports update state to the user needs all three answers, not one.
/// </remarks>
/// <param name="Update">The newer release, or null when there is none.</param>
/// <param name="Error">Why the check failed. Null when it succeeded.</param>
/// <param name="LastSuccessUtc">
/// When a check last reached GitHub — this one, or an earlier one if this
/// attempt failed. Null when none ever has.
/// </param>
public sealed record UpdateCheckOutcome(
    UpdateInfo? Update,
    string? Error,
    DateTimeOffset? LastSuccessUtc)
{
    public bool Succeeded => Error is null;

    public bool UpToDate => Succeeded && Update is null;
}
