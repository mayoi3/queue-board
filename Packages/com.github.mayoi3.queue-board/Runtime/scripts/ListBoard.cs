// ListBoard.cs
// UdonSharp v1+ / Worlds SDK3
// 仕様: 1ペイン / 10件×ページ / 行=ボタンで(済)トグル / Joinは末尾追加 / Leaveは置換表示

using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using TMPro;
using VRC.Udon.Common.Interfaces;
using VRC.SDK3.UdonNetworkCalling;
using VRC.Udon.Common;

namespace MayoiWorks.QueueBoard
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ListBoard : UdonSharpBehaviour
    {
        [Header("Config")]
        public int Max = 100;                                 // 上限100
        [Tooltip("離脱行の表示文言")]
        public string LeaveText = "[列から抜けました]";
        [Tooltip("Join後に自動で最終ページへ移動")]
        public bool AutoGoLastPageOnJoin = true;

        [Header("UI: Rows (上から0..9)")]
        public Button[] rowButtons;                           // 10本（OnClick → Row側プロキシから ToggleBySlot(slot) 呼び出し）

        private TextMeshProUGUI[] rowNums;
        private Image[] rowChecks;
        private TextMeshProUGUI[] rowNames;

        private string numChildName = "Num";
        private string checkChildName = "Check";
        private string nameChildName = "Name";

        [Header("UI: Help")]
        public GameObject helpPanel; // 説明文のGameObject（同期不要・ローカル表示）

        [Header("Checkbox Sprites (optional)")]
        public Sprite checkOnSprite;                           // ✓用
        public Sprite checkOffSprite;                          // □用（未設定時は色で代替）

        [Header("UI: Pager & Status")]
        public Button btnPrevPage;
        public Button btnNextPage;
        public TextMeshProUGUI pageInfoText;                   // 例: "1/3"

        [Header("UI: Join/Leave")]
        public Button btnJoin;
        public Button btnLeave;
        public TextMeshProUGUI yourNumberText;                 // 任意: "あなたの番号: #12" / 未登録
        public Button btnPending;                             // 同期待ち表示用（非操作）

        // ====== 同期データ ======
        [UdonSynced] private short[] entries;                // 0=未使用 / 上位1bit=done, 下位15bit=playerId
        [UdonSynced] private int revision;                  // 世代カウンタ（Single-writerのみ増加）

        // ====== ローカル ======
        private const int PageSize = 10;
        private int offset = 0;                                // 10刻み（ページ先頭インデックス）
        // 表示用のワーキングコピー（古い受信をUIに反映しない）
        private short[] viewEntries;
        private int localRevision = -1;
        private bool pendingAutoGoLast = false;               // 自分がJoinを要求した直後の自動ページ送り用フラグ
        private int pendingAction = 0;                        // 0=なし, 1=Join, 2=Leave
        private float pendingTimeoutSeconds = 5f;              // ローディングの自動解除秒数（0以下で無効）
        private float pendingSince = 0f;

        // 送信デバウンス
        private float sendDebounceSeconds = 0.25f;
        private bool dirtyQueued = false;
        private float nextSendAt = 0f;

        // ====== ライフサイクル ======
        void Start()
        {
            AutoBindRowParts();                                // RowButtons から子参照をキャッシュ
            if (Networking.IsOwner(gameObject))
            {
                EnsureArrays();
                // 初期オーナーのみ必要最小限。即送信はしない
            }
            EnsureViewArrays();
            CopyToView();
            RefreshUI();
        }

        public override void OnDeserialization()
        {
            // 同期変数(entries/revision)は既に適用済み。このタイミングでUI用に採用可否を判定
            if (revision >= localRevision)
            {
                localRevision = revision;
                CopyToView();
                ApplyPostReceiveLocalEffects();
            }
            ClampOffset();
            RefreshUI();
        }

        public override void OnOwnershipTransferred(VRCPlayerApi newOwner)
        {
            if (Networking.IsOwner(gameObject))
            {
                EnsureArrays();
                // オーナー直後に送信はしない（巻き戻り抑止）
                EnsureViewArrays();
                CopyToView();
                RefreshUI();
            }
        }

        // ====== 内部ユーティリティ ======
        private void EnsureArrays()
        {
            if (entries == null || entries.Length != Max) entries = new short[Max];
        }

        private void EnsureViewArrays()
        {
            if (viewEntries == null || viewEntries.Length != Max) viewEntries = new short[Max];
        }

        private void CopyToView()
        {
            EnsureArrays();
            EnsureViewArrays();
            for (int i = 0; i < Max; i++)
            {
                viewEntries[i] = entries[i];
            }
        }

        private bool ArraysReady()
        {
            return entries != null && entries.Length == Max;
        }

        private int LastUsedIndex()
        {
            if (entries == null) return -1;
            for (int i = entries.Length - 1; i >= 0; i--)
                if (entries[i] != 0) return i;
            return -1;
        }

        private int LastUsedIndexView()
        {
            if (viewEntries == null) return -1;
            for (int i = viewEntries.Length - 1; i >= 0; i--)
                if (viewEntries[i] != 0) return i;
            return -1;
        }

        // entries から playerId を探索
        private int FindByPlayerId(int playerId)
        {
            if (entries == null || playerId <= 0) return -1;
            int pid = MaskPid(playerId);
            for (int i = 0; i < entries.Length; i++)
            {
                short e = entries[i];
                if (e == 0) continue;
                if (GetPid(e) == pid) return i;
            }
            return -1;
        }

        private int FindByPlayerIdView(int playerId)
        {
            if (viewEntries == null || playerId <= 0) return -1;
            int pid = MaskPid(playerId);
            for (int i = 0; i < viewEntries.Length; i++)
            {
                short e = viewEntries[i];
                if (e == 0) continue;
                if (GetPid(e) == pid) return i;
            }
            return -1;
        }

        private bool IsJoinedViewByPlayerId(int playerId)
        {
            return FindByPlayerIdView(playerId) != -1;
        }

        private int GetJoinedIndexViewByPlayerId(int playerId)
        {
            return FindByPlayerIdView(playerId);
        }

        private int FindAppendRow()
        {
            int last = LastUsedIndex();
            int next = last + 1;
            return (entries != null && next < entries.Length) ? next : -1;
        }

        private void Sync()
        {
            // 直接即送信は行わず、オーナーでのみデバウンス送信
            if (!Networking.IsOwner(gameObject)) return;
            dirtyQueued = true;
            float now = Time.time;
            if (nextSendAt < now)
                nextSendAt = now + sendDebounceSeconds;
        }

        void Update()
        {
            // フォールバック: OnDeserializationが飛んだ場合でもrevision差分でUI更新
            if (revision > localRevision)
            {
                localRevision = revision;
                CopyToView();
                ApplyPostReceiveLocalEffects();
                RefreshUI();
            }

            // ペンディングのタイムアウト解除
            if (pendingAction != 0 && pendingTimeoutSeconds > 0f && (Time.time - pendingSince) > pendingTimeoutSeconds)
            {
                pendingAction = 0;
                RefreshUI();
            }

            // 以降はオーナーのみ: デバウンス送信
            if (!Networking.IsOwner(gameObject)) return;
            if (!dirtyQueued) return;
            if (Time.time < nextSendAt) return;

            revision++;
            dirtyQueued = false;
            RequestSerialization();
            // 送信直後、ローカル表示も更新
            localRevision = revision;
            CopyToView();
            ApplyPostReceiveLocalEffects();
            RefreshUI();
        }

        private void ClampOffset()
        {
            if (offset < 0) offset = 0;
            int last = LastUsedIndex();
            if (last < 0) { offset = 0; return; }
            int lastPageStart = (last / PageSize) * PageSize;
            if (offset > lastPageStart) offset = lastPageStart;
        }

        private string Pad2(int n)
        {
            return (n < 100) ? n.ToString("D2") : n.ToString();
        }

        // ====== 自動バインド ======
        private void AutoBindRowParts()
        {
            int n = (rowButtons == null) ? 0 : rowButtons.Length;
            rowNums = new TextMeshProUGUI[n];
            rowChecks = new Image[n];
            rowNames = new TextMeshProUGUI[n];

            for (int i = 0; i < n; i++)
            {
                Button btn = rowButtons[i];
                if (!btn) continue;

                Transform t = btn.transform;
                Transform tfNum = FindDirectChild(t, numChildName);
                Transform tfCheck = FindDirectChild(t, checkChildName);
                Transform tfName = FindDirectChild(t, nameChildName);

                if (tfNum) rowNums[i] = tfNum.GetComponent<TextMeshProUGUI>();
                if (tfCheck) rowChecks[i] = tfCheck.GetComponent<Image>();
                if (tfName) rowNames[i] = tfName.GetComponent<TextMeshProUGUI>();
            }
        }

        private Transform FindDirectChild(Transform parent, string childName)
        {
            if (parent == null || childName == null || childName == "") return null;
            int c = parent.childCount;
            for (int i = 0; i < c; i++)
            {
                Transform ch = parent.GetChild(i);
                if (ch != null && ch.name == childName) return ch;
            }
            return null;
        }

        // ====== Public UI Events ======
        public void BtnJoin()
        {
            VRCPlayerApi lp = Networking.LocalPlayer; if (lp == null) return;
            int mePid = lp.playerId;
            if (IsJoinedViewByPlayerId(mePid)) { RefreshUI(); return; }
            if (AutoGoLastPageOnJoin) pendingAutoGoLast = true;
            pendingAction = 1;
            pendingSince = Time.time;
            RefreshUI(); // 即時にpending表示
			SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReqJoin));
        }

        public void BtnLeave()
        {
            VRCPlayerApi lp = Networking.LocalPlayer; if (lp == null) return;
            int mePid = lp.playerId;
            pendingAction = 2;
            pendingSince = Time.time;
            RefreshUI(); // 即時にpending表示
			SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReqLeave));
        }

        public void BtnPagePrev()
        {
            offset -= PageSize;
            if (offset < 0) offset = 0;
            RefreshUI();
        }

        public void BtnPageNext()
        {
            int last = LastUsedIndexView(); if (last < 0) return;
            int totalPages = (last / PageSize) + 1;
            int curPage = (offset / PageSize) + 1;
            int nextPage = Mathf.Min(curPage + 1, totalPages);
            offset = (nextPage - 1) * PageSize;
            RefreshUI();
        }

        // 行ボタン（0..9）から呼ばれる
        public void ToggleBySlot(int slot)
        {
            if (pendingAction != 0 && !Networking.IsOwner(gameObject)) return; // 同期待ちの非オーナーは操作不可
            int i = offset + slot;
            int last = LastUsedIndexView();
            if (i > last) return;
            if (viewEntries == null || viewEntries[i] == 0) return;
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReqToggle), i + 1);
        }

        // ====== UI描画 ======
        private void RefreshUI()
        {
            // Join/Leave の出し分けと番号表示
            if (btnJoin || btnLeave || btnPending || yourNumberText)
            {
                bool ready = ArraysReady();
                VRCPlayerApi lp = Networking.LocalPlayer;
                int mePid = (lp == null) ? 0 : lp.playerId;
                bool joined = ready && mePid > 0 && IsJoinedViewByPlayerId(mePid);
                bool isPending = (pendingAction != 0);

                if (btnPending) { btnPending.gameObject.SetActive(isPending); if (btnPending) btnPending.interactable = false; }
                if (btnJoin) btnJoin.gameObject.SetActive(!isPending && ready && !joined);
                if (btnLeave) btnLeave.gameObject.SetActive(!isPending && ready && joined);

                if (yourNumberText)
                {
                    if (!ready || mePid == 0)
                    {
                        yourNumberText.text = "";
                    }
                    else
                    {
                        int ix = GetJoinedIndexViewByPlayerId(mePid);
                        yourNumberText.text = (ix == -1) ? "" : ("あなたの番号: #" + (ix + 1));
                    }
                }
            }

            // ページ情報とボタン活性
            int lastIdx = LastUsedIndexView();
            if (lastIdx < 0)
            {
                offset = 0;
                if (pageInfoText) pageInfoText.text = "1/1";
                if (btnPrevPage) btnPrevPage.interactable = false;
                if (btnNextPage) btnNextPage.interactable = false;

                // 空表示
                for (int s = 0; s < PageSize; s++)
                    SetRow(slot: s, hasData: false, interactable: false, index1Based: 0, isDone: false, displayName: "", isLeave: false);
                return;
            }

            int totalPages = (lastIdx / PageSize) + 1;
            int cur = (offset / PageSize) + 1;
            if (cur > totalPages) cur = totalPages;
            if (pageInfoText) pageInfoText.text = cur + "/" + totalPages;
            if (btnPrevPage) btnPrevPage.interactable = (cur > 1);
            if (btnNextPage) btnNextPage.interactable = (cur < totalPages);

            // 行描画
            for (int slot = 0; slot < PageSize; slot++)
            {
                int i = offset + slot;

                if (i > lastIdx || viewEntries[i] == 0)
                {
                    SetRow(slot, false, false, 0, false, "", false);
                    continue;
                }

                short e = viewEntries[i];
                bool isLeave = IsLeaveEntry(e);
                bool isDone = IsDone(e);
                int pid = GetPid(e);
                VRCPlayerApi p = VRCPlayerApi.GetPlayerById(pid);
                string disp = isLeave ? LeaveText : ((p == null) ? "" : p.displayName);
                bool isOwner = Networking.IsOwner(gameObject);
                bool isPending = (pendingAction != 0);
                bool rowsInteractable = isOwner || !isPending; // 非オーナーは自分がpendingの間は行操作不可
                SetRow(slot, true, rowsInteractable, i + 1, isDone, disp, isLeave);
            }
        }

        private void ApplyPostReceiveLocalEffects()
        {
            // Join完了後の自動最終ページ移動と保留解除
            VRCPlayerApi lp = Networking.LocalPlayer; if (lp == null) return;
            int mePid = lp.playerId;
            if (pendingAutoGoLast && IsJoinedViewByPlayerId(mePid))
            {
                int last = LastUsedIndexView();
                offset = (last / PageSize) * PageSize;
                pendingAutoGoLast = false;
            }
            if (pendingAction == 1 && IsJoinedViewByPlayerId(mePid)) pendingAction = 0; // Join反映
            if (pendingAction == 2 && !IsJoinedViewByPlayerId(mePid)) pendingAction = 0; // Leave反映
        }

        public override void OnPostSerialization(SerializationResult result)
        {
            // ネットワーク送信後（試行後）の実バイト数と成功可否のみを出力
            Debug.Log("[QueueBoard] OnPostSerialization: success=" + (result.success ? "true" : "false") + ", bytes=" + result.byteCount);
        }

        // ====== Owner-side handlers (Single-writer) ======
        private const short DoneMask = (short)-32768; // bit15
        private const short LeavePid = (short)0x7FFF; // 下位15bitの全ビット=1 を離脱PIDとして予約

        private bool IsDone(short entry)
        {
            return (entry & DoneMask) != 0;
        }

        private bool IsLeaveEntry(short entry)
        {
            return GetPid(entry) == LeavePid;
        }

        private short MakeEntry(int playerId, bool doneFlag)
        {
            int pid = MaskPid(playerId);
            return (short)(pid | (doneFlag ? DoneMask : (short)0));
        }

        private int GetPid(short entry)
        {
            return entry & 0x7FFF;
        }

        private int MaskPid(int playerId)
        {
            return playerId & 0x7FFF;
        }

        private short ToggleDone(short entry)
        {
            return (short)(entry ^ DoneMask);
        }

		[NetworkCallable]
		public void ReqJoin()
		{
			if (!Networking.IsOwner(gameObject)) return;
			VRCPlayerApi caller = NetworkCalling.CallingPlayer;
			if (caller == null) return;
			int playerId = caller.playerId;
			if (playerId <= 0) return;
			EnsureArrays();

			// 既にJoin済みなら何もしない
			if (FindByPlayerId(playerId) != -1) return;
			int row = FindAppendRow(); if (row == -1) return;

			entries[row] = MakeEntry(playerId, false);
			Sync();
			// Owner: 楽観的に即時UI反映（ネットワーク送信はデバウンスで後追い）
			CopyToView();
			ApplyPostReceiveLocalEffects();
			RefreshUI();
		}

		[NetworkCallable]
		public void ReqLeave()
		{
			if (!Networking.IsOwner(gameObject)) return;
			VRCPlayerApi caller = NetworkCalling.CallingPlayer;
			if (caller == null) return;
			int playerId = caller.playerId;
			if (playerId <= 0) return;
			EnsureArrays();

			int ix = FindByPlayerId(playerId); if (ix == -1) return;
			// 離脱マーカーへ置換（done状態は維持）
			bool d = IsDone(entries[ix]);
			entries[ix] = MakeEntry(LeavePid, d);
			Sync();
			// Owner: 楽観的に即時UI反映
			CopyToView();
			ApplyPostReceiveLocalEffects();
			RefreshUI();
		}

        [NetworkCallable]
        public void ReqToggle(int index1Based)
        {
            if (!Networking.IsOwner(gameObject)) return;
            EnsureArrays();
            int i = index1Based - 1;
            if (i < 0 || i >= Max) return;
            if (entries[i] == 0) return;
            entries[i] = ToggleDone(entries[i]);
            Sync();
            // Owner: 楽観的に即時UI反映
            CopyToView();
            RefreshUI();
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (player == null)
            {
                Debug.Log("[QueueBoard] OnPlayerJoined: player is null");
                return;
            }
            Debug.Log("[QueueBoard] OnPlayerJoined: playerId=" + player.playerId + ", name=" + player.displayName);
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (!Networking.IsOwner(gameObject)) return;
            if (player == null) return;
            EnsureArrays();
            int ix = FindByPlayerId(player.playerId);
            if (ix == -1) return;
            bool d = IsDone(entries[ix]);
            entries[ix] = MakeEntry(LeavePid, d); // 左詰めせず、離脱マーカー
            Sync();
            CopyToView();
            RefreshUI();
        }

        // Row の各要素を個別に更新（Num / Check / Name）
        private void SetRow(int slot, bool hasData, bool interactable, int index1Based, bool isDone, string displayName, bool isLeave)
        {
            // Row Button
            if (rowButtons != null && slot < rowButtons.Length && rowButtons[slot] != null)
            {
                rowButtons[slot].interactable = interactable && hasData;
                rowButtons[slot].gameObject.SetActive(true); // 常に占位
            }

            // Num
            if (rowNums != null && slot < rowNums.Length && rowNums[slot] != null)
            {
                rowNums[slot].text = hasData ? (Pad2(index1Based) + ".") : "—";
            }

            // Check (Image)
            if (rowChecks != null && slot < rowChecks.Length && rowChecks[slot] != null)
            {
                Image img = rowChecks[slot];
                if (!hasData)
                {
                    img.enabled = false;
                }
                else
                {
                    img.enabled = true;
                    if (checkOnSprite != null && checkOffSprite != null)
                    {
                        img.sprite = isDone ? checkOnSprite : checkOffSprite;
                        img.color = Color.white;
                    }
                    else
                    {
                        // スプライト未指定時は色だけで表現（緑/グレー）
                        img.sprite = null;
                        img.color = isDone ? new Color(0.24f, 0.86f, 0.52f, 1f) : new Color(0.58f, 0.65f, 0.73f, 1f);
                    }
                }
            }

            // Name（RichTextはInspectorでONにしておく）
            if (rowNames != null && slot < rowNames.Length && rowNames[slot] != null)
            {
                if (!hasData)
                {
                    rowNames[slot].text = "";
                }
                else
                {
                    rowNames[slot].text = isLeave ? ("<color=#9CA3AF>" + displayName + "</color>") : displayName;
                }
            }
        }

        public void BtnHelpToggle()
        {
            if (helpPanel != null) helpPanel.SetActive(!helpPanel.activeSelf);
        }
    }
}