using System;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>Where the endpoints and triggers ask for runs; the runs themselves happen one at a time, elsewhere.</summary>
internal interface IAutoCollectionQueue
{
    /// <summary>Asks for a run of one definition. Its state shows <c>Queued</c> at once.</summary>
    /// <param name="request">The run.</param>
    /// <returns>A task that ends with the run (its state), or faults as the run does.</returns>
    Task<AutoCollectionState> RequestAsync(AutoCollectionRequest request);

    /// <summary>Asks for a pass over every enabled definition.</summary>
    /// <param name="reason">What starts it.</param>
    /// <param name="progress">Told the share of the pass done, from 0 to 100, or <c>null</c>.</param>
    /// <returns>A task that ends with the pass.</returns>
    Task RequestPassAsync(SmartSearchAutoCollectionRunReason reason, IProgress<double>? progress = null);

    /// <summary>
    /// What is happening to a definition now: <c>Queued</c> (with the reason <c>WaitingForScan</c> while only a pass that
    /// waits for a library scan has it), <c>Running</c>, or nothing (<c>null</c>).
    /// </summary>
    /// <param name="definitionId">The definition's id.</param>
    /// <returns>The activity, or <c>null</c>.</returns>
    AutoCollectionActivity? ActivityOf(Guid definitionId);
}
