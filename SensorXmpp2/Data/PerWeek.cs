using Waher.Persistence.Attributes;

namespace SensorXmpp.Data
{
	/// <summary>
	/// Historic record, sampled every week
	/// </summary>
	[CollectionName("HistoryPerWeek")]
	public class PerWeek : HistoricRecordWithPeaks
	{
	}
}
