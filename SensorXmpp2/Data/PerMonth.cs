using Waher.Persistence.Attributes;

namespace SensorXmpp.Data
{
	/// <summary>
	/// Historic record, sampled every month
	/// </summary>
	[CollectionName("HistoryPerMonth")]
	public class PerMonth : HistoricRecordWithPeaks
	{
	}
}
