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

namespace MayoiWorks.QueueBoard
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ListBoard : UdonSharpBehaviour
    {
        private const string VERSION = "2.1.0-beta1";

        [Header("Config")]
        public int Max = 100;                                 // 上限100
        [Tooltip("離脱行の表示文言")]
        public string LeaveText = "[列から抜けました]";
        [Tooltip("同期待ち中の表示文言")]
        public string SyncPendingText = "[同期待ち中...]";
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

        [Header("UI: Debug")]
        public TextMeshProUGUI debugText; // デバッグ情報表示用（任意）

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
        [UdonSynced] private string[] names;                   // ""=未使用, LeaveText=離脱, それ以外=表示名
        [UdonSynced] private byte[] done;                   // 0/1（サイズ削減）
        [UdonSynced] private short revision;                // 世代カウンタ（Single-writerのみ増加）

        // ====== ローカル ======
        private const int PageSize = 10;
        private int offset = 0;                                // 10刻み（ページ先頭インデックス）
        // 表示用のワーキングコピー（古い受信をUIに反映しない）
        private string[] viewNames;
        private byte[] viewDone;
        private short[] viewRevision;                         // 各セルの最終更新revision（巻き戻り防止）
        private short localRevision = -1;                      // グローバルrevision追跡用
        private bool pendingAutoGoLast = false;               // 自分がJoinを要求した直後の自動ページ送り用フラグ
        private int pendingAction = 0;                        // 0=なし, 1=Join, 2=Leave
        private float pendingTimeoutSeconds = 5f;              // ローディングの自動解除秒数（0以下で無効）
        private float pendingSince = 0f;

        private const float MinDebounceSeconds = 0.25f;
        private const float MaxDebounceSeconds = 30.0f;
        private float sendDebounceSeconds = MinDebounceSeconds;
        private bool dirtyQueued = false;
        private float nextSendAt = 0f;

        // ====== デバッグ情報追跡 ======
        private float lastUdonSyncedDelay = 0f;              // 最後のUdonSynced遅延（秒）
        private int lastSyncByteCount = 0;                   // 最後のUdonSynced送信バイト数
        private short lastReceivedEventRevision = 0;         // 最後に受信したイベントrevision
        private int eventDropCount = 0;                      // イベントドロップ回数
        private int eventReceiveCount = 0;                   // イベント受信回数
        private float lastEventDelay = 0f;                   // 最後のイベント遅延（秒）

        // ====== ライフサイクル ======
        void Start()
        {
            AutoBindRowParts();                                // RowButtons から子参照をキャッシュ
            if (Networking.IsOwner(gameObject))
            {
                EnsureArrays();
                // 初期オーナーのみ1回だけRequestSerialization
                RequestSerialization();
            }
            EnsureViewArrays();
            CopyToView();
            RefreshUI();
            UpdateDebugDisplay();
        }

        public override void OnDeserialization(VRC.Udon.Common.DeserializationResult result)
        {
            // 同期変数(names/done/revision)は既に適用済み
            // 各セルについて、UdonSyncedの方が新しければ採用
            EnsureArrays();
            EnsureViewArrays();
            
            // デバッグ：UdonSynced遅延計算
            lastUdonSyncedDelay = Time.realtimeSinceStartup - result.sendTime;
            
            for (int i = 0; i < Max; i++)
            {
                if (revision > viewRevision[i])
                {
                    viewNames[i] = names[i];
                    viewDone[i] = done[i];
                    viewRevision[i] = revision;
                }
            }
            
            if (revision > localRevision)
                localRevision = revision;
            
            ApplyPostReceiveLocalEffects();
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

        public override void OnPostSerialization(VRC.Udon.Common.SerializationResult result)
        {
            // デバッグ：送信バイト数記録（Ownerのみ）
            if (result.success)
            {
                lastSyncByteCount = result.byteCount;
            }
        }

        // ====== 内部ユーティリティ ======
        private void EnsureArrays()
        {
            if (names == null || names.Length != Max) names = new string[Max];
            if (done == null || done.Length != Max) done = new byte[Max];
        }

        private void EnsureViewArrays()
        {
            if (viewNames == null || viewNames.Length != Max) viewNames = new string[Max];
            if (viewDone == null || viewDone.Length != Max) viewDone = new byte[Max];
            if (viewRevision == null || viewRevision.Length != Max) viewRevision = new short[Max];
        }

        private void CopyToView()
        {
            EnsureArrays();
            EnsureViewArrays();
            for (int i = 0; i < Max; i++)
            {
                viewNames[i] = names[i];
                viewDone[i] = done[i];
                viewRevision[i] = revision;
            }
        }

        private bool ArraysReady()
        {
            return names != null && done != null && names.Length == Max && done.Length == Max;
        }

        private int LastUsedIndex()
        {
            if (names == null) return -1;
            for (int i = names.Length - 1; i >= 0; i--)
                if (!string.IsNullOrEmpty(names[i])) return i; // LeaveText も「使用中」
            return -1;
        }

        private int LastUsedIndexView()
        {
            if (viewNames == null) return -1;
            for (int i = viewNames.Length - 1; i >= 0; i--)
                if (!string.IsNullOrEmpty(viewNames[i])) return i;
            return -1;
        }

        // トランケート前後のいずれかで一致するインデックスを返す（names配列）
        private int FindByDisplayNameAny(string original)
        {
            if (names == null || string.IsNullOrEmpty(original)) return -1;
            // 完全一致を優先
            for (int i = 0; i < names.Length; i++)
                if (names[i] == original) return i;
            // 短縮名でも照合
            string truncated = TruncateUtf8(original, MaxDisplayNameUtf8Bytes, TruncateSuffix);
            if (truncated == original) return -1;
            for (int i = 0; i < names.Length; i++)
                if (names[i] == truncated) return i;
            return -1;
        }

        // トランケート前後のいずれかで一致するインデックスを返す（viewNames配列）
        private int FindByDisplayNameAnyView(string original)
        {
            if (viewNames == null || string.IsNullOrEmpty(original)) return -1;
            for (int i = 0; i < viewNames.Length; i++)
                if (viewNames[i] == original) return i;
            string truncated = TruncateUtf8(original, MaxDisplayNameUtf8Bytes, TruncateSuffix);
            if (truncated == original) return -1;
            for (int i = 0; i < viewNames.Length; i++)
                if (viewNames[i] == truncated) return i;
            return -1;
        }

        // 表示名が現在Join済み（LeaveTextではない）とみなせるか
        private bool IsJoinedViewByDisplayName(string original)
        {
            int ix = FindByDisplayNameAnyView(original);
            if (ix == -1) return false;
            string v = viewNames[ix];
            return !string.IsNullOrEmpty(v) && v != LeaveText;
        }

        // Join済みならそのインデックス（0-based）、未登録やLeaveなら -1
        private int GetJoinedIndexViewByDisplayName(string original)
        {
            int ix = FindByDisplayNameAnyView(original);
            if (ix == -1) return -1;
            string v = viewNames[ix];
            if (string.IsNullOrEmpty(v) || v == LeaveText) return -1;
            return ix;
        }

        private int FindAppendRow()
        {
            int last = LastUsedIndex();
            int next = last + 1;
            return (names != null && next < names.Length) ? next : -1;
        }

        private void Sync()
        {
            // 直接即送信は行わず、オーナーでのみデバウンス送信
            if (!Networking.IsOwner(gameObject)) return;
            
            // Suffering監視してデバウンス時間を動的調整
            AdjustDebounceTime();
            
            dirtyQueued = true;
            float now = Time.time;
            if (nextSendAt < now)
                nextSendAt = now + sendDebounceSeconds;
        }
        
        private void AdjustDebounceTime()
        {
            float suffering = VRC.SDK3.Network.Stats.Suffering;
            
            if (suffering > 50f)
            {
                // 負荷が高い：デバウンス時間を2倍に（最大30秒）
                sendDebounceSeconds = Mathf.Min(sendDebounceSeconds * 2f, MaxDebounceSeconds);
            }
            else
            {
                // 負荷が低い：デバウンス時間を1/2に（最小0.25秒）
                sendDebounceSeconds = Mathf.Max(sendDebounceSeconds / 2f, MinDebounceSeconds);
            }
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

            // revision++はReq系メソッドで既に実行済み
            dirtyQueued = false;
            RequestSerialization();
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
            string me = lp.displayName;
            if (IsJoinedViewByDisplayName(me)) { RefreshUI(); return; }
            if (AutoGoLastPageOnJoin) pendingAutoGoLast = true;
            pendingAction = 1;
            pendingSince = Time.time;
            RefreshUI(); // 即時にpending表示
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReqJoin));
        }

        public void BtnLeave()
        {
            VRCPlayerApi lp = Networking.LocalPlayer; if (lp == null) return;
            string me = lp.displayName;
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
            if (viewNames == null || string.IsNullOrEmpty(viewNames[i])) return;
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
                string me = (lp == null) ? "" : lp.displayName;
                bool joined = ready && me != "" && IsJoinedViewByDisplayName(me);
                bool isPending = (pendingAction != 0);

                if (btnPending) { btnPending.gameObject.SetActive(isPending); if (btnPending) btnPending.interactable = false; }
                if (btnJoin) btnJoin.gameObject.SetActive(!isPending && ready && !joined);
                if (btnLeave) btnLeave.gameObject.SetActive(!isPending && ready && joined);

                if (yourNumberText)
                {
                    if (!ready || me == "")
                    {
                        yourNumberText.text = "";
                    }
                    else
                    {
                        int ix = GetJoinedIndexViewByDisplayName(me);
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

                if (i > lastIdx)
                {
                    SetRow(slot, false, false, 0, false, "", false);
                    continue;
                }

                // 歯抜け判定：末尾より前なのに名前が空 = 同期待ち中
                bool isSyncPending = (i < lastIdx && string.IsNullOrEmpty(viewNames[i]));
                
                if (string.IsNullOrEmpty(viewNames[i]) && !isSyncPending)
                {
                    // 末尾かつ空 = データなし
                    SetRow(slot, false, false, 0, false, "", false);
                    continue;
                }

                string displayName = isSyncPending ? SyncPendingText : viewNames[i];
                bool isLeave = (!isSyncPending && viewNames[i] == LeaveText);
                bool isDone = (viewDone[i] == 1);
                bool isOwner = Networking.IsOwner(gameObject);
                bool isPending = (pendingAction != 0);
                bool rowsInteractable = isOwner || !isPending; // 非オーナーは自分がpendingの間は行操作不可
                SetRow(slot, true, rowsInteractable, i + 1, isDone, displayName, isLeave);
            }
            
            // デバッグ表示更新
            UpdateDebugDisplay();
        }

        private void ApplyPostReceiveLocalEffects()
        {
            // Join完了後の自動最終ページ移動と保留解除
            VRCPlayerApi lp = Networking.LocalPlayer; if (lp == null) return;
            string me = lp.displayName;
            if (pendingAutoGoLast && IsJoinedViewByDisplayName(me))
            {
                int last = LastUsedIndexView();
                offset = (last / PageSize) * PageSize;
                pendingAutoGoLast = false;
            }
            if (pendingAction == 1 && IsJoinedViewByDisplayName(me)) pendingAction = 0; // Join反映
            if (pendingAction == 2 && !IsJoinedViewByDisplayName(me)) pendingAction = 0; // Leave反映
        }

        // ====== Owner-side handlers (Single-writer) ======
        private int MaxDisplayNameUtf8Bytes = 32;
        private string TruncateSuffix = "…";

        // イベント送信ヘルパー（Join操作）
        private void BroadcastJoin(int index, int playerId, short rev)
        {
            int sendTimeMs = Networking.GetServerTimeInMilliseconds();
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(OnBoardUpdateCell), 
                (short)index, playerId, (byte)0, rev, sendTimeMs);
        }

        // イベント送信ヘルパー（Leave操作）
        private void BroadcastLeave(int index, short rev)
        {
            int sendTimeMs = Networking.GetServerTimeInMilliseconds();
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(OnBoardUpdateCell), 
                (short)index, -1, (byte)0, rev, sendTimeMs);
        }

        // イベント送信ヘルパー（Toggle操作）
        private void BroadcastToggle(int index, byte newDone, short rev)
        {
            int sendTimeMs = Networking.GetServerTimeInMilliseconds();
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(OnBoardUpdateCell), 
                (short)index, 0, newDone, rev, sendTimeMs);
        }

        [NetworkCallable]
        public void ReqJoin()
        {
            if (!Networking.IsOwner(gameObject)) return;
            
            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (caller == null) return;
            
            string displayName = caller.displayName;
            int playerId = caller.playerId;
            
            if (string.IsNullOrEmpty(displayName)) return;
            EnsureArrays();

            // 既にJoin済み（短縮後含む）なら何もしない
            if (FindByDisplayNameAny(displayName) != -1) return;
            int row = FindAppendRow(); if (row == -1) return;

            names[row] = TruncateUtf8(displayName, MaxDisplayNameUtf8Bytes, TruncateSuffix);
            done[row] = 0;
            revision++;
            
            Sync();
            BroadcastJoin(row, playerId, revision);
            
            // Owner: 楽観的に即時UI反映
            localRevision = revision;
            CopyToView();
            ApplyPostReceiveLocalEffects();
            RefreshUI();
        }

        private string TruncateUtf8(string s, int maxBytes, string suffix)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (maxBytes <= 0) return s;
            var enc = System.Text.Encoding.UTF8;
            int total = enc.GetByteCount(s);
            if (total <= maxBytes) return s;
            int suffixBytes = string.IsNullOrEmpty(suffix) ? 0 : enc.GetByteCount(suffix);
            int budget = Mathf.Max(0, maxBytes - suffixBytes);
            int len = s.Length;
            int end = 0;
            for (int i = 0; i < len; i++)
            {
                int next = i + 1;
                if (char.IsHighSurrogate(s[i]) && next < len && char.IsLowSurrogate(s[next])) next++;
                int bytes = enc.GetByteCount(s.Substring(0, next));
                if (bytes > budget) break;
                end = next;
                i = next - 1;
            }
            if (end <= 0) return string.IsNullOrEmpty(suffix) ? string.Empty : suffix;
            return string.IsNullOrEmpty(suffix) ? s.Substring(0, end) : (s.Substring(0, end) + suffix);
        }

        [NetworkCallable]
        public void ReqLeave()
        {
            if (!Networking.IsOwner(gameObject)) return;
            
            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (caller == null) return;
            
            string displayName = caller.displayName;
            
            if (string.IsNullOrEmpty(displayName)) return;
            EnsureArrays();

            // 短縮後を含めて対象行を特定
            int ix = FindByDisplayNameAny(displayName); if (ix == -1) return;
            
            names[ix] = LeaveText; // done は触らない
            revision++;
            
            Sync();
            BroadcastLeave(ix, revision);
            
            // Owner: 楽観的に即時UI反映
            localRevision = revision;
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
            if (string.IsNullOrEmpty(names[i])) return;
            
            byte newDone = (byte)(done[i] == 0 ? 1 : 0);
            done[i] = newDone;
            revision++;
            
            // トグル時は5回に1回だけUdonSynced送信（頻度削減）
            if (revision % 5 == 0)
            {
                Sync();
            }
            BroadcastToggle(i, newDone, revision);
            
            // Owner: 楽観的に即時UI反映
            localRevision = revision;
            CopyToView();
            RefreshUI();
        }

        // ====== イベント受信ハンドラ ======
        [NetworkCallable]
        public void OnBoardUpdateCell(short index, int playerId, byte done, short eventRevision, int sendTimeMs)
        {
            // Ownerは自身で既に更新済み
            if (Networking.IsOwner(gameObject)) return;
            
            // デバッグ：イベント遅延計算
            double currentTime = (double)Networking.GetServerTimeInMilliseconds() / 1000.0;
            double sendTime = (double)sendTimeMs / 1000.0;
            lastEventDelay = (float)Networking.CalculateServerDeltaTime(currentTime, sendTime);
            
            // デバッグ：イベントドロップレート計算
            if (lastReceivedEventRevision > 0)
            {
                short expectedRevision = (short)(lastReceivedEventRevision + 1);
                short gap = (short)(eventRevision - expectedRevision);
                if (gap > 0)
                {
                    eventDropCount += gap;
                }
            }
            lastReceivedEventRevision = eventRevision;
            eventReceiveCount++;
            
            EnsureViewArrays();
            if (index < 0 || index >= Max) return;
            
            // このセルへの更新が古ければ無視（巻き戻り防止）
            if (viewRevision[index] >= eventRevision) return;
            
            // 操作種別を判定して適用
            if (playerId > 0)
            {
                // Join: playerIdから表示名を取得
                VRCPlayerApi player = VRCPlayerApi.GetPlayerById(playerId);
                if (player == null) return; // プレイヤー既に退出
                
                string displayName = player.displayName;
                viewNames[index] = TruncateUtf8(displayName, MaxDisplayNameUtf8Bytes, TruncateSuffix);
                viewDone[index] = 0;
            }
            else if (playerId == -1)
            {
                // Leave: LeaveTextに設定
                viewNames[index] = LeaveText;
                // viewDone[index]は変更しない
            }
            else // playerId == 0
            {
                // Toggle: doneのみ更新
                viewDone[index] = done;
                // viewNames[index]は変更しない
            }
            
            viewRevision[index] = eventRevision;
            
            if (eventRevision > localRevision)
                localRevision = eventRevision;
            
            ApplyPostReceiveLocalEffects();
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

        // ====== デバッグ表示 ======
        private void UpdateDebugDisplay()
        {
            if (debugText == null) return;
            
            VRCPlayerApi owner = Networking.GetOwner(gameObject);
            string ownerName = owner != null ? owner.displayName : "None";
            
            // イベントドロップレート計算
            float eventDropRate = 0f;
            if (eventReceiveCount > 0)
            {
                eventDropRate = (float)eventDropCount / (eventReceiveCount + eventDropCount);
            }
            
            // Suffering取得
            float suffering = VRC.SDK3.Network.Stats.Suffering;
            bool isOwner = Networking.IsOwner(gameObject);
                        
            // デバッグ情報を組み立て
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine($"[Debug Info] Version: {VERSION}");
            sb.AppendLine($"Owner: {ownerName}, Suff: {suffering:F0}, EVQ: {NetworkCalling.GetAllQueuedEvents()}");
            
            if (isOwner)
            {
                // Ownerのみ：送信側の統計
                sb.AppendLine($"Debounce: {sendDebounceSeconds:F0}s, Size: {lastSyncByteCount} bytes");
            }
            else
            {
                // 非Ownerのみ：受信側の統計
                sb.AppendLine($"US Delay: {lastUdonSyncedDelay:F0}s, EV Delay: {lastEventDelay:F0}s, Drop: {eventDropRate:P1}");
            }
            
            debugText.text = sb.ToString();
        }
    }
}