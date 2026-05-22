using System;
using Mirror;

namespace RPG.Quest
{

    [Serializable]
    public struct QuestProgress : IEquatable<QuestProgress>
    {
        public string QuestId;
        public QuestState State;
        public string ProgressCsv;
        public long StateTimestamp;

        public bool Equals(QuestProgress other)
            => QuestId == other.QuestId
            && State == other.State
            && ProgressCsv == other.ProgressCsv
            && StateTimestamp == other.StateTimestamp;

        public override bool Equals(object obj)
            => obj is QuestProgress p && Equals(p);

        public override int GetHashCode()
            => unchecked((QuestId?.GetHashCode() ?? 0)
                ^ ((int)State * 397)
                ^ (ProgressCsv?.GetHashCode() ?? 0));

        public static QuestProgress NewActive(string questId, int objectiveCount)
        {
            return new QuestProgress
            {
                QuestId        = questId,
                State          = QuestState.Active,
                ProgressCsv    = SerializeProgress(new int[objectiveCount]),
                StateTimestamp = NowUnix()
            };
        }

        public int[] GetProgressArray(int expectedCount)
        {
            return DeserializeProgress(ProgressCsv, expectedCount);
        }

        public QuestProgress WithProgress(int[] progress, QuestState newState = QuestState.Active)
        {
            var copy = this;
            copy.ProgressCsv = SerializeProgress(progress);

            // Só atualiza timestamp se o state realmente muda
            if (copy.State != newState)
            {
                copy.State          = newState;
                copy.StateTimestamp = NowUnix();
            }
            else
            {
                copy.State = newState;
            }

            return copy;
        }

        // === FIX (Lote 3): só atualiza timestamp em mudança real de state ===
        public QuestProgress WithState(QuestState newState)
        {
            var copy = this;
            if (copy.State != newState)
            {
                copy.State          = newState;
                copy.StateTimestamp = NowUnix();
            }
            return copy;
        }

        public static string SerializeProgress(int[] arr)
        {
            if (arr == null || arr.Length == 0) return "";
            var sb = new System.Text.StringBuilder(arr.Length * 3);
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(arr[i]);
            }
            return sb.ToString();
        }

        public static int[] DeserializeProgress(string csv, int expectedCount)
        {
            var result = new int[expectedCount];
            if (string.IsNullOrEmpty(csv) || expectedCount == 0) return result;

            var parts = csv.Split(',');
            int limit = parts.Length < expectedCount ? parts.Length : expectedCount;
            for (int i = 0; i < limit; i++)
            {
                if (int.TryParse(parts[i], out int v) && v >= 0)
                    result[i] = v;
            }
            return result;
        }

        private static long NowUnix()
            => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public enum QuestState : byte
    {
        Active        = 0,
        ReadyToTurnIn = 1,
        Completed     = 2,
        Failed        = 3,
    }
}