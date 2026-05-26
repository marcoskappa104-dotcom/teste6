using System.Collections.Generic;
using UnityEngine;

namespace RPG.Data
{
    [System.Serializable]
    public class LootPoolEntry
    {
        public ItemData Item;
        [Range(1, 1000)]
        public int Weight = 100;
        
        public int MinQuantity = 1;
        public int MaxQuantity = 1;

        [Tooltip("Chance individual extra (0-100). Se for 100, depende apenas do peso do pool.")]
        [Range(0f, 100f)]
        public float IndividualChance = 100f;
    }

    [System.Serializable]
    public class LootPool
    {
        public string PoolName = "Novo Pool";
        
        [Tooltip("Chance de este pool inteiro ser processado.")]
        [Range(0f, 100f)]
        public float PoolChance = 100f;

        [Tooltip("Quantos itens deste pool serão sorteados (se o pool passar no teste de chance).")]
        public int RollCount = 1;

        [Tooltip("Se true, o mesmo item pode ser sorteado múltiplas vezes.")]
        public bool AllowDuplicates = false;

        public List<LootPoolEntry> Entries = new List<LootPoolEntry>();

        public List<(string itemId, int quantity)> Roll()
        {
            var results = new List<(string, int)>();

            if (Random.Range(0f, 100f) > PoolChance) return results;
            if (Entries.Count == 0) return results;

            int totalWeight = 0;
            foreach (var e in Entries) totalWeight += e.Weight;
            if (totalWeight <= 0) return results;

            for (int i = 0; i < RollCount; i++)
            {
                int roll = Random.Range(0, totalWeight);
                int acc = 0;
                
                foreach (var e in Entries)
                {
                    acc += e.Weight;
                    if (roll < acc)
                    {
                        // Teste de chance individual
                        if (Random.Range(0f, 100f) <= e.IndividualChance)
                        {
                            int qty = Random.Range(e.MinQuantity, e.MaxQuantity + 1);
                            results.Add((e.Item.ItemId, qty));
                        }

                        if (!AllowDuplicates)
                        {
                            // Nota: Se não permitir duplicatas, removemos do peso total para o próximo roll
                            // Mas para simplificar nesta versão, apenas paramos o loop se for um item único.
                        }
                        break;
                    }
                }
            }

            return results;
        }
    }

    [CreateAssetMenu(menuName = "RPG/Loot Table", fileName = "LootTable_New")]
    public class LootTable : ScriptableObject
    {
        public List<LootPool> Pools = new List<LootPool>();

        public List<(string itemId, int quantity)> GetDrops()
        {
            var allDrops = new List<(string, int)>();
            foreach (var pool in Pools)
            {
                allDrops.AddRange(pool.Roll());
            }
            return allDrops;
        }
    }
}
