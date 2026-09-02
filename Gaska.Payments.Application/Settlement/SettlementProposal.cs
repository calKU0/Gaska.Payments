using Gaska.Payments.Domain.Model;

namespace Gaska.Payments.Application.Settlement;

/// <summary>A row to be written to <c>pay.Payment</c>.</summary>
/// <param name="Result">The match result (empty for payments already settled in ERP).</param>
/// <param name="ErpEntryId">The correlated cash or bank entry in ERP (CDN.Zapisy.KAZ_GIDNumer).</param>
/// <param name="Status">A state from <see cref="SettlementStatus"/>.</param>
/// <param name="RegisterSeries">The bank register the cash entry will go to.</param>
/// <param name="PostingCategory">Kind of operation - it decides the cash operation symbol.</param>
public sealed record SettlementProposal(
    MatchResult Result, int? ErpEntryId, string Status, string? RegisterSeries, string PostingCategory);
