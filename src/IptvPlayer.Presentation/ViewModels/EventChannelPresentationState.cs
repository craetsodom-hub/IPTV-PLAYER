using System;

namespace IptvPlayer.Presentation.ViewModels;

internal static class EventChannelPresentationState
{
    public static bool IsLoading(bool hasSelectedEvent, bool isPresentationLoading, Guid? matchingSourceId, Guid? matchedSourceId)
    {
        if (hasSelectedEvent)
        {
            if (!isPresentationLoading)
            {
                if (matchingSourceId.HasValue)
                {
                    if (matchedSourceId.HasValue != matchingSourceId.HasValue)
                    {
                        return true;
                    }
                    if (!matchedSourceId.HasValue)
                    {
                        return false;
                    }
                    return matchedSourceId.GetValueOrDefault() != matchingSourceId.GetValueOrDefault();
                }
                return false;
            }
            return true;
        }
        return false;
    }

    public static bool IsNoMatchVisible(bool hasSelectedEvent, bool hasFinalMatchResult, bool hasVerifiedOptions, bool isPresentationLoading, Guid? matchingSourceId, Guid? matchedSourceId)
    {
        if (hasSelectedEvent && hasFinalMatchResult && !hasVerifiedOptions)
        {
            return !IsLoading(hasSelectedEvent, isPresentationLoading, matchingSourceId, matchedSourceId);
        }
        return false;
    }
}
