using System;
using SaintCoinach.Xiv;
using SaintCoinach.Xiv.ItemActions;

namespace FFXIVBisSolver
{
    public class FoodItem
    {
        public FoodItem(Item item)
        {
            Item = item;
            if (!(Item.ItemAction is Food))
            {
                throw new ArgumentException("Item is not a food item", "item");
            }
            Food = (Food) Item.ItemAction;
        }

        public Item Item { get; set; }
        public Food Food { get; set; }

        public static bool IsFoodItem(Item item)
        {
            return item.ItemAction is Food;
        }

        public override string ToString()
        {
            return Item.ToString();
        }
    }
}