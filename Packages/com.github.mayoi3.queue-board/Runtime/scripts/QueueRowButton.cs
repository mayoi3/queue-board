using UdonSharp;
using UnityEngine;
using MayoiWorks.QueueBoard;

namespace MayoiWorks.QueueBoard
{
    public class QueueRowButton : UdonSharpBehaviour
    {
        public ListBoard board;

        [Tooltip("このボタンのスロット (0..9)")] public int slot;
        public void OnClick()
        {
            if (board != null) board.ToggleBySlot(slot);
        }
    }
}