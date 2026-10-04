using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using HarmonyLib;
using Debug = LiftoffGhostLines.ModLog;
using static LiftoffGhostLines.GhostLinesPlugin;
using static LiftoffGhostLines.Prefs;

namespace LiftoffGhostLines
{
    public partial class GhostLinePoller
    {
        public bool TryAttachLeaderboardSearch(Scene scene)
        {
            if (leaderboardSearchInput != null)
                return true;
            if (!scene.IsValid() || !scene.isLoaded)
                return false;

            Dropdown dropdown = FindLbTrackDropdown(scene);
            if (dropdown == null)
                return false;

            leaderboardController = dropdown.GetComponentInParent<MonoBehaviour>();
            leaderboardContentDropdown = dropdown;
            SetLeaderboardEnvironmentDropdown(GetLeaderboardEnvironmentDropdown(leaderboardController));
            leaderboardNotFoundLogged = false;
            if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Direct leaderboard dropdown found: " + GetTransformPath(dropdown.transform));
            CreateLeaderboardSearchRow();
            return leaderboardSearchInput != null;
        }

        public bool AttachLeaderboardSearchFromController(Component controller)
        {
            if (controller == null)
                return false;

            // Never touch the leaderboard search UI outside its own scene: the
            // game's Dropdown.Hide() selects/deselects and steals chat focus.
            if (IsContentSceneActive() || !IsLeaderboardSceneLoaded())
                return false;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            System.Reflection.FieldInfo field = controller.GetType().GetField("dropdownContentFilter", flags);
            Dropdown dropdown = field != null ? field.GetValue(controller) as Dropdown : null;
            if (dropdown == null)
            {
                if (!leaderboardNotFoundLogged)
                {
                    leaderboardNotFoundLogged = true;
                    if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Leaderboard controller has no content dropdown yet");
                }
                return false;
            }

            if (leaderboardSearchReady && leaderboardContentDropdown != null && leaderboardContentDropdown != dropdown)
                CleanupLeaderboardSearch();

            leaderboardController = controller;
            leaderboardContentDropdown = dropdown;
            SetLeaderboardEnvironmentDropdown(GetLeaderboardEnvironmentDropdown(controller));
            leaderboardNotFoundLogged = false;
            if (leaderboardSearchReady && leaderboardSearchInput != null)
                return true;
            if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Attaching from controller dropdown: " + GetTransformPath(dropdown.transform));
            CreateLeaderboardSearchRow();
            return leaderboardSearchInput != null;
        }

        private Dropdown GetLeaderboardEnvironmentDropdown(Component controller)
        {
            if (controller == null)
                return null;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            System.Reflection.FieldInfo field = controller.GetType().GetField("dropdownEnvironmentFilter", flags);
            return field != null ? field.GetValue(controller) as Dropdown : null;
        }

        private void SetLeaderboardEnvironmentDropdown(Dropdown dropdown)
        {
            if (leaderboardEnvironmentDropdown == dropdown)
            {
                if (dropdown != null && leaderboardEnvironmentValue != dropdown.value)
                    OnLeaderboardEnvironmentChanged(dropdown.value);
                return;
            }

            if (leaderboardEnvironmentDropdown != null)
                leaderboardEnvironmentDropdown.onValueChanged.RemoveListener(OnLeaderboardEnvironmentChanged);

            leaderboardEnvironmentDropdown = dropdown;
            leaderboardEnvironmentValue = dropdown != null ? dropdown.value : -1;
            if (dropdown != null)
                dropdown.onValueChanged.AddListener(OnLeaderboardEnvironmentChanged);
        }

        private void OnLeaderboardEnvironmentChanged(int value)
        {
            leaderboardEnvironmentValue = value;
            leaderboardContentRefreshPending = true;
            leaderboardPendingContentSignature = "";
            leaderboardPendingContentStableFrames = 0;
            leaderboardContentOptions.Clear();
            leaderboardDisplayedSignature = "";
            leaderboardSearchFocusLock = false;
            leaderboardSearchSelectedText = "";
            HideLeaderboardSearchPopup();

            if (leaderboardSearchInput != null)
            {
                leaderboardSearchUpdating = true;
                leaderboardSearchInput.text = "";
                leaderboardSearchUpdating = false;
            }

            leaderboardSearchTimer = 0f;
            if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Leaderboard environment changed; refreshing content tracks");
        }

        public void TickLeaderboardSearch()
        {
            // The leaderboard search UI must only exist while the leaderboard
            // screen is the ACTIVE scene. The persisted (DontDestroyOnLoad)
            // controller keeps its Update running in the flight scene, and the
            // game's Dropdown.Hide() calls Selectable.Select(), which would
            // otherwise repeatedly steal keyboard focus from the chat field
            // (breaking MP chat after a replay round-trip).
            if (IsContentSceneActive() || !IsLeaderboardSceneLoaded())
            {
                if (leaderboardContentDropdown != null || leaderboardSearchInput != null || leaderboardController != null)
                    CleanupLeaderboardSearch(false);
                return;
            }

            if (leaderboardContentDropdown == null &&
                (leaderboardSearchInput != null || leaderboardController != null))
            {
                CleanupLeaderboardSearch();
            }

            if (!leaderboardContentRefreshPending && Time.unscaledTime < leaderboardSearchTimer)
                return;
            leaderboardSearchTimer = Time.unscaledTime + (leaderboardContentRefreshPending ? 0.05f : 1f);

            int sceneCountNow = SceneManager.sceneCount;
            if (sceneCountNow != leaderboardFindSceneCount)
            {
                leaderboardFindSceneCount = sceneCountNow;
                leaderboardFindFailures = 0;
            }

            if (leaderboardContentDropdown == null || leaderboardEnvironmentDropdown == null)
            {
                FindLeaderboardContentDropdown();
                if (leaderboardContentDropdown == null)
                {
                    leaderboardFindFailures++;
                    leaderboardSearchTimer = Time.unscaledTime
                        + Mathf.Min(20f, 2f * (1 << Mathf.Min(leaderboardFindFailures, 4)));
                    return;
                }
                leaderboardFindFailures = 0;
            }
            if (leaderboardContentDropdown == null)
                return;

            if (leaderboardEnvironmentDropdown != null &&
                leaderboardEnvironmentValue != leaderboardEnvironmentDropdown.value)
            {
                OnLeaderboardEnvironmentChanged(leaderboardEnvironmentDropdown.value);
            }

            if (leaderboardSearchInput == null)
                CreateLeaderboardSearchRow();
            if (leaderboardSearchInput == null)
                return;

            string signature = GetDropdownSignature(leaderboardContentDropdown.options);
            if (leaderboardContentRefreshPending)
            {
                if (signature == leaderboardPendingContentSignature)
                    leaderboardPendingContentStableFrames++;
                else
                {
                    leaderboardPendingContentSignature = signature;
                    leaderboardPendingContentStableFrames = 0;
                }

                if (leaderboardPendingContentStableFrames < 2)
                    return;

                leaderboardContentOptions = new List<Dropdown.OptionData>(leaderboardContentDropdown.options);
                leaderboardContentRefreshPending = false;
                ApplyLeaderboardSearch();
            }
            else if (signature != leaderboardDisplayedSignature && !leaderboardSearchUpdating)
            {
                leaderboardContentOptions = new List<Dropdown.OptionData>(leaderboardContentDropdown.options);
                ApplyLeaderboardSearch();
            }
        }

        public static bool IsLeaderboardSceneLoaded()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name.IndexOf("Leaderboard", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        // Flight/replay content scenes use 32-char hex names (e.g.
        // "9a0fd962d2856964e96f79df01058b83"). While one is active, the
        // leaderboard search UI must stay completely dormant: the persisted
        // leaderboard controller keeps updating, and the game's Dropdown.Hide()
        // calls Selectable.Select(), which steals the chat field's keyboard focus.
        public static bool IsContentSceneActive()
        {
            try
            {
                string name = SceneManager.GetActiveScene().name;
                if (string.IsNullOrEmpty(name) || name.Length != 32)
                    return false;
                for (int i = 0; i < name.Length; i++)
                {
                    char c = name[i];
                    bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                    if (!hex)
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void FindLeaderboardContentDropdown()
        {
            MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>(true);
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            int contentFilterFields = 0;
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null)
                    continue;
                System.Type type = behaviour.GetType();
                var contentField = type.GetField("dropdownContentFilter", flags);
                if (contentField == null)
                    continue;
                contentFilterFields++;

                if (type.GetField("dropdownLeaderboardType", flags) == null ||
                    type.GetField("dropdownEnvironmentFilter", flags) == null ||
                    type.GetField("dropdownLeaderboardArchive", flags) == null)
                    continue;

                try
                {
                    Dropdown dropdown = contentField.GetValue(behaviour) as Dropdown;
                    if (dropdown != null)
                    {
                        leaderboardController = behaviour;
                        leaderboardContentDropdown = dropdown;
                        SetLeaderboardEnvironmentDropdown(GetLeaderboardEnvironmentDropdown(behaviour));
                        if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Found leaderboard controller: " + type.FullName +
                            "; dropdown=" + GetTransformPath(dropdown.transform));
                        return;
                    }
                }
                catch { }
            }
            if (!leaderboardNotFoundLogged)
            {
                leaderboardNotFoundLogged = true;
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Leaderboard controller NOT found: behaviours=" + behaviours.Length +
                    ", content-filter fields=" + contentFilterFields);
            }
        }

        private string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            System.Text.StringBuilder path = new System.Text.StringBuilder(transform.name);
            Transform current = transform.parent;
            while (current != null)
            {
                path.Insert(0, current.name + "/");
                current = current.parent;
            }
            return path.ToString();
        }

        private void CreateLeaderboardSearchRow()
        {
            if (leaderboardContentDropdown == null || leaderboardSearchInputObject != null || leaderboardSearchReady)
                return;

            GameObject dropdownObject = leaderboardContentDropdown.gameObject;
            Graphic sourceGraphic = leaderboardContentDropdown.targetGraphic;
            leaderboardOriginalDropdownEnabled = leaderboardContentDropdown.enabled;
            leaderboardOriginalDropdownValue = leaderboardContentDropdown.value;
            Text inputText = leaderboardContentDropdown.captionText;
            if (inputText == null)
                inputText = dropdownObject.GetComponentInChildren<Text>(true);
            if (inputText == null)
            {
                if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Cannot create search input: content dropdown has no caption Text");
                CleanupLeaderboardSearch();
                return;
            }

            leaderboardOriginalContentText = inputText;
            leaderboardOriginalContentTextActive = inputText.gameObject.activeSelf;
            inputText.gameObject.SetActive(false);

            GameObject inputObject = new GameObject(
                "GhostLines_LeaderboardContentSearchInput",
                typeof(RectTransform),
                typeof(Image),
                typeof(InputField),
                typeof(LeaderboardSearchInputRelay));
            inputObject.transform.SetParent(dropdownObject.transform, false);
            inputObject.transform.SetAsLastSibling();
            RectTransform inputRect = inputObject.GetComponent<RectTransform>();
            inputRect.anchorMin = Vector2.zero;
            inputRect.anchorMax = Vector2.one;
            inputRect.offsetMin = new Vector2(10f, 0f);
            inputRect.offsetMax = new Vector2(-34f, 0f);
            inputRect.pivot = new Vector2(0.5f, 0.5f);

            Image inputBackground = inputObject.GetComponent<Image>();
            inputBackground.color = new Color(1f, 1f, 1f, 0.001f);
            inputBackground.raycastTarget = true;

            Text searchText = CreateSearchText(inputText, inputObject.transform, "SearchText");
            searchText.text = "";
            searchText.raycastTarget = false;

            Text placeholder = CreateSearchText(inputText, inputObject.transform, "SearchPlaceholder");
            placeholder.text = "Search content...";
            placeholder.color = new Color(0.55f, 0.55f, 0.55f, 1f);
            placeholder.raycastTarget = false;

            InputField input = inputObject.GetComponent<InputField>();
            input.targetGraphic = inputBackground;
            input.textComponent = searchText;
            input.placeholder = placeholder;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 128;
            input.interactable = true;
            input.navigation = new Navigation { mode = Navigation.Mode.Automatic };
            input.text = GetCurrentLeaderboardContentText();
            input.onValueChanged.AddListener(OnLeaderboardSearchChanged);
            leaderboardContentDropdown.onValueChanged.AddListener(OnLeaderboardContentSelected);
            leaderboardSearchInput = input;
            leaderboardSearchInputObject = inputObject;
            LeaderboardSearchInputRelay relay = inputObject.GetComponent<LeaderboardSearchInputRelay>();
            relay.owner = this;

            leaderboardContentOptions = new List<Dropdown.OptionData>(leaderboardContentDropdown.options);
            leaderboardDisplayedSignature = GetDropdownSignature(leaderboardContentDropdown.options);
            leaderboardSearchSelectedText = GetCurrentLeaderboardContentText();
            leaderboardSearchReady = true;
            RectTransform dropdownRect = dropdownObject.GetComponent<RectTransform>();
            if (ModLog.DebugEnabled) Debug.Log("[GhostLines] Added autocomplete input over original content dropdown: dropdown=" +
                (dropdownRect != null ? dropdownRect.rect.size.ToString() : "<no-rect>") +
                "; input=" + inputRect.rect.size.ToString());
        }

        private Text CreateSearchText(Text source, Transform parent, string name)
        {
            GameObject textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);
            RectTransform rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(14f, 0f);
            rect.offsetMax = new Vector2(-14f, 0f);
            Text text = textObject.GetComponent<Text>();
            text.font = source.font;
            text.fontSize = source.fontSize;
            text.fontStyle = source.fontStyle;
            text.alignment = source.alignment;
            text.horizontalOverflow = source.horizontalOverflow;
            text.verticalOverflow = source.verticalOverflow;
            text.lineSpacing = source.lineSpacing;
            text.supportRichText = source.supportRichText;
            text.color = source.color;
            text.raycastTarget = false;
            return text;
        }

        private void OnLeaderboardSearchChanged(string value)
        {
            if (leaderboardSearchUpdating)
                return;
            if (!leaderboardSearchUpdating)
                leaderboardSearchFocusLock = true;
            bool keepInputFocus = leaderboardSearchInput != null &&
                (leaderboardSearchInput.isFocused ||
                 (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == leaderboardSearchInput.gameObject));
            ApplyLeaderboardSearch();
            OpenLeaderboardSearchDropdown(keepInputFocus);
        }

        private string GetCurrentLeaderboardContentText()
        {
            if (leaderboardContentDropdown == null || leaderboardContentDropdown.options.Count == 0)
                return "";
            int index = Mathf.Clamp(leaderboardContentDropdown.value, 0, leaderboardContentDropdown.options.Count - 1);
            Dropdown.OptionData option = leaderboardContentDropdown.options[index];
            return option != null ? option.text : "";
        }

        private void BeginLeaderboardSearchInput()
        {
            if (leaderboardSearchInput == null || leaderboardSearchStarting)
                return;

            leaderboardSearchFocusLock = true;
            leaderboardSearchStarting = true;
            try
            {
                if (string.Equals(leaderboardSearchInput.text, leaderboardSearchSelectedText, System.StringComparison.Ordinal))
                {
                    leaderboardSearchUpdating = true;
                    leaderboardSearchInput.text = "";
                    leaderboardSearchUpdating = false;
                    ApplyLeaderboardSearch();
                }

                leaderboardSearchInput.ActivateInputField();
                OpenLeaderboardSearchDropdown(true);
            }
            finally
            {
                leaderboardSearchStarting = false;
            }
        }

        private void OpenLeaderboardSearchDropdown(bool keepInputFocus)
        {
            if (leaderboardContentDropdown == null || !leaderboardContentDropdown.IsActive() ||
                !leaderboardContentDropdown.IsInteractable())
                return;

            try
            {
                GameObject popup;
                System.Collections.IList items;
                if (!TryGetLeaderboardPopup(out popup, out items))
                {
                    leaderboardPopupLayoutCached = false;
                    leaderboardContentDropdown.Show();
                }

                if (keepInputFocus)
                    leaderboardSearchRefocusPending = true;

                ApplyLeaderboardSearch();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[GhostLines] Could not open filtered content dropdown: " + ex.Message);
            }
        }

        private void HideLeaderboardSearchPopup()
        {
            HideLeaderboardSearchPopup(true);
        }

        private void HideLeaderboardSearchPopup(bool hideDropdown)
        {
            if (leaderboardContentDropdown != null && hideDropdown)
            {
                try { leaderboardContentDropdown.Hide(); } catch { }
            }

            leaderboardPopupLayoutCached = false;
            leaderboardPopupFirstItemX = 0f;
            leaderboardPopupLastItemY = 0f;
            leaderboardPopupRowHeight = 0f;
            leaderboardPopupBaseContentHeight = 0f;
            leaderboardSearchRefocusPending = false;
            if (leaderboardSearchRefocusCoroutine != null)
            {
                StopCoroutine(leaderboardSearchRefocusCoroutine);
                leaderboardSearchRefocusCoroutine = null;
            }
        }

        private void OnLeaderboardContentSelected(int value)
        {
            if (leaderboardContentDropdown == null || leaderboardSearchInput == null || leaderboardSearchUpdating)
                return;
            if (value < 0 || value >= leaderboardContentDropdown.options.Count)
                return;

            leaderboardSearchFocusLock = false;

            Dropdown.OptionData selectedOption = leaderboardContentDropdown.options[value];
            leaderboardSearchSelectedText = selectedOption != null ? selectedOption.text : "";

            leaderboardSearchUpdating = true;
            try
            {
                leaderboardSearchInput.text = leaderboardSearchSelectedText;
                leaderboardOriginalDropdownValue = value;
                leaderboardDisplayedSignature = GetDropdownSignature(leaderboardContentDropdown.options);
                leaderboardPopupLayoutCached = false;
            }
            finally
            {
                leaderboardSearchUpdating = false;
            }
        }

        private void ApplyLeaderboardSearch()
        {
            if (leaderboardContentDropdown == null || leaderboardSearchInput == null || leaderboardSearchUpdating)
                return;

            leaderboardSearchUpdating = true;
            try
            {
                string query = (leaderboardSearchInput.text ?? "").Trim();
                // Rank each option once, then stable-sort by rank (ties keep the
                // original order, as the old insertion sort did).
                var ranked = new List<KeyValuePair<int, Dropdown.OptionData>>();
                foreach (var option in leaderboardContentOptions)
                {
                    int rank = GetSearchRank(option != null ? option.text : "", query);
                    if (rank >= 0)
                        ranked.Add(new KeyValuePair<int, Dropdown.OptionData>(rank, option));
                }
                List<Dropdown.OptionData> filtered = new List<Dropdown.OptionData>(ranked.Count);
                foreach (var kv in System.Linq.Enumerable.OrderBy(ranked, kv => kv.Key))
                    filtered.Add(kv.Value);

                string fullSignature = GetDropdownSignature(leaderboardContentOptions);
                leaderboardDisplayedSignature = fullSignature;
                RefreshLeaderboardPopup(filtered);
            }
            finally
            {
                leaderboardSearchUpdating = false;
            }
        }

        private bool TryGetLeaderboardPopup(out GameObject popup, out System.Collections.IList items)
        {
            popup = null;
            items = null;
            if (leaderboardContentDropdown == null)
                return false;

            if (leaderboardDropdownPopupField == null || leaderboardDropdownItemsField == null)
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic;
                System.Type dropdownType = typeof(Dropdown);
                leaderboardDropdownPopupField = dropdownType.GetField("m_Dropdown", flags);
                leaderboardDropdownItemsField = dropdownType.GetField("m_Items", flags);

                System.Type itemType = dropdownType.GetNestedType("DropdownItem",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (itemType != null)
                {
                    leaderboardPopupItemRectProperty = itemType.GetProperty("rectTransform", flags | System.Reflection.BindingFlags.Public);
                    leaderboardPopupItemTextProperty = itemType.GetProperty("text", flags | System.Reflection.BindingFlags.Public);
                }
            }

            if (leaderboardDropdownPopupField == null || leaderboardDropdownItemsField == null)
                return false;

            try
            {
                popup = leaderboardDropdownPopupField.GetValue(leaderboardContentDropdown) as GameObject;
                items = leaderboardDropdownItemsField.GetValue(leaderboardContentDropdown) as System.Collections.IList;
                return popup != null && items != null && items.Count > 0;
            }
            catch
            {
                popup = null;
                items = null;
                return false;
            }
        }

        private void RefreshLeaderboardPopup(List<Dropdown.OptionData> filtered)
        {
            GameObject popup;
            System.Collections.IList items;
            if (!TryGetLeaderboardPopup(out popup, out items) || leaderboardPopupItemRectProperty == null)
                return;

            if (items.Count != leaderboardContentOptions.Count)
            {
                HideLeaderboardSearchPopup();
                return;
            }

            // Original index -> position in the filtered list.
            var originalIndexOf = new Dictionary<Dropdown.OptionData, int>();
            for (int i = 0; i < leaderboardContentOptions.Count; i++)
            {
                Dropdown.OptionData o = leaderboardContentOptions[i];
                if (o != null && !originalIndexOf.ContainsKey(o))
                    originalIndexOf[o] = i;
            }
            var visiblePositionOf = new Dictionary<int, int>(filtered.Count);
            foreach (Dropdown.OptionData option in filtered)
            {
                int originalIndex;
                if (option != null && originalIndexOf.TryGetValue(option, out originalIndex) && !visiblePositionOf.ContainsKey(originalIndex))
                    visiblePositionOf[originalIndex] = visiblePositionOf.Count;
            }
            int visibleCount = visiblePositionOf.Count;

            RectTransform popupRect = popup.GetComponent<RectTransform>();
            RectTransform firstItemRect = GetLeaderboardPopupItemRect(items[0]);
            RectTransform contentRect = firstItemRect != null ? firstItemRect.parent as RectTransform : null;
            if (popupRect == null || firstItemRect == null || contentRect == null)
                return;

            RectTransform lastItemRect = GetLeaderboardPopupItemRect(items[items.Count - 1]);
            if (lastItemRect == null)
                return;

            if (!leaderboardPopupLayoutCached)
            {
                leaderboardPopupLayoutCached = true;
                leaderboardPopupFirstItemX = firstItemRect.anchoredPosition.x;
                leaderboardPopupLastItemY = lastItemRect.anchoredPosition.y;
                leaderboardPopupRowHeight = Mathf.Max(firstItemRect.rect.height, 1f);
                leaderboardPopupBaseContentHeight = contentRect.rect.height - leaderboardPopupRowHeight * items.Count;
            }

            for (int i = 0; i < items.Count; i++)
            {
                RectTransform itemRect = GetLeaderboardPopupItemRect(items[i]);
                if (itemRect == null)
                    continue;

                int visiblePosition;
                bool visible = visiblePositionOf.TryGetValue(i, out visiblePosition);
                itemRect.gameObject.SetActive(visible);
                if (!visible)
                    continue;

                itemRect.anchoredPosition = new Vector2(
                    leaderboardPopupFirstItemX,
                    leaderboardPopupLastItemY + leaderboardPopupRowHeight * (visibleCount - 1 - visiblePosition));

                if (leaderboardPopupItemTextProperty != null)
                {
                    Text itemText = leaderboardPopupItemTextProperty.GetValue(items[i], null) as Text;
                    if (itemText != null)
                        itemText.text = leaderboardContentOptions[i] != null ? leaderboardContentOptions[i].text : "";
                }
            }

            float contentHeight = Mathf.Max(
                0f,
                Mathf.Max(0f, leaderboardPopupBaseContentHeight) + leaderboardPopupRowHeight * visibleCount);
            contentRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight);

            ScrollRect scrollRect = popup.GetComponentInChildren<ScrollRect>(true);
            if (scrollRect != null && scrollRect.content != null && scrollRect.content != contentRect)
                scrollRect.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight);
            if (scrollRect != null)
                scrollRect.verticalNormalizedPosition = 1f;
        }

        private RectTransform GetLeaderboardPopupItemRect(object item)
        {
            if (item == null || leaderboardPopupItemRectProperty == null)
                return null;
            try
            {
                return leaderboardPopupItemRectProperty.GetValue(item, null) as RectTransform;
            }
            catch
            {
                return null;
            }
        }

        private int GetSearchRank(string text, string query)
        {
            if (string.IsNullOrEmpty(query))
                return 0;

            text = text ?? "";
            if (string.Equals(text, query, System.StringComparison.OrdinalIgnoreCase))
                return 0;
            if (text.StartsWith(query, System.StringComparison.OrdinalIgnoreCase))
                return 1;

            string[] words = text.Split(new[] { ' ', '-', '_', '/', '\\', '.', '(', ')' },
                System.StringSplitOptions.RemoveEmptyEntries);
            foreach (string word in words)
            {
                if (word.StartsWith(query, System.StringComparison.OrdinalIgnoreCase))
                    return 2;
            }

            return text.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0 ? 3 : -1;
        }

        private string GetDropdownSignature(List<Dropdown.OptionData> options)
        {
            System.Text.StringBuilder signature = new System.Text.StringBuilder();
            foreach (var option in options)
                signature.Append(option != null ? option.text : "").Append('\u001f');
            return signature.ToString();
        }

        private void CleanupLeaderboardSearch()
        {
            CleanupLeaderboardSearch(true);
        }

        private void CleanupLeaderboardSearch(bool hidePopup)
        {
            if (leaderboardSearchInput != null)
                leaderboardSearchInput.onValueChanged.RemoveListener(OnLeaderboardSearchChanged);
            if (leaderboardEnvironmentDropdown != null)
                leaderboardEnvironmentDropdown.onValueChanged.RemoveListener(OnLeaderboardEnvironmentChanged);
            if (leaderboardContentDropdown != null)
            {
                leaderboardContentDropdown.onValueChanged.RemoveListener(OnLeaderboardContentSelected);
                HideLeaderboardSearchPopup(hidePopup);
                leaderboardContentDropdown.enabled = leaderboardOriginalDropdownEnabled;
            }
            if (leaderboardOriginalContentText != null)
                leaderboardOriginalContentText.gameObject.SetActive(leaderboardOriginalContentTextActive);
            if (leaderboardSearchInputObject != null)
                Destroy(leaderboardSearchInputObject);
            leaderboardSearchInputObject = null;
            leaderboardSearchInput = null;
            leaderboardContentDropdown = null;
            leaderboardEnvironmentDropdown = null;
            leaderboardEnvironmentValue = -1;
            leaderboardContentRefreshPending = false;
            leaderboardPendingContentSignature = "";
            leaderboardPendingContentStableFrames = 0;
            leaderboardController = null;
            leaderboardContentOptions.Clear();
            leaderboardDisplayedSignature = "";
            leaderboardSearchSelectedText = "";
            leaderboardOriginalContentText = null;
            leaderboardOriginalContentTextActive = false;
            leaderboardOriginalDropdownEnabled = false;
            leaderboardOriginalDropdownValue = 0;
            leaderboardSearchStarting = false;
            leaderboardSearchRefocusPending = false;
            leaderboardSearchFocusLock = false;
            leaderboardPopupLayoutCached = false;
            leaderboardPopupFirstItemX = 0f;
            leaderboardPopupLastItemY = 0f;
            leaderboardPopupRowHeight = 0f;
            leaderboardPopupBaseContentHeight = 0f;
            if (leaderboardSearchRefocusCoroutine != null)
            {
                StopCoroutine(leaderboardSearchRefocusCoroutine);
                leaderboardSearchRefocusCoroutine = null;
            }
            EndLeaderboardDragSelection();
            leaderboardSelectedTogglesQueue.Clear();
            leaderboardCachedScrollViewContent = null;
            leaderboardCachedScrollRect = null;
            leaderboardScrollViewFallbackExhausted = false;
            leaderboardSearchUpdating = false;
            leaderboardSearchTimer = 0f;
            leaderboardNotFoundLogged = false;
            leaderboardSearchReady = false;
        }

        // Reused by the per-frame drag-select tick.
        private readonly List<LeaderboardRowItem> leaderboardDragRows = new List<LeaderboardRowItem>();

        private struct LeaderboardRowItem
        {
            public int index;
            public Toggle toggle;
            public RectTransform rowRect;
            public RectTransform toggleRect;
        }

        private Transform FindLeaderboardScrollViewContent()
        {
            if (leaderboardCachedScrollViewContent != null)
            {
                if (leaderboardCachedScrollViewContent.gameObject.activeInHierarchy)
                    return leaderboardCachedScrollViewContent;
                return null;
            }

            if (Time.unscaledTime < leaderboardScrollViewContentSearchTimer)
                return null;
            leaderboardScrollViewContentSearchTimer = Time.unscaledTime + 0.5f;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                GameObject[] roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    if (root == null) continue;
                    Transform target = root.transform.Find("LeaderboardSelection/panelLeaderboard/Content/Scroll View/Viewport/scrollViewContent");
                    if (target == null && root.name == "Canvas")
                    {
                        target = root.transform.Find("LeaderboardSelection/panelLeaderboard/Content/Scroll View/Viewport/scrollViewContent");
                    }
                    if (target != null)
                    {
                        leaderboardCachedScrollViewContent = target;
                        leaderboardCachedScrollRect = target.GetComponentInParent<ScrollRect>();
                        if (target.gameObject.activeInHierarchy)
                            return target;
                        return null;
                    }
                }
            }

            if (!leaderboardScrollViewFallbackExhausted)
            {
                leaderboardScrollViewFallbackExhausted = true;
                GameObject[] allObjects = FindObjectsOfType<GameObject>();
                foreach (var go in allObjects)
                {
                    if (go.name == "scrollViewContent" && go.activeInHierarchy)
                    {
                        if (go.transform.parent != null && go.transform.parent.name == "Viewport")
                        {
                            leaderboardCachedScrollViewContent = go.transform;
                            leaderboardCachedScrollRect = go.GetComponentInParent<ScrollRect>();
                            return go.transform;
                        }
                    }
                }
            }

            return null;
        }

        private void TickLeaderboardDragSelection()
        {
            // Only drive the drag-select while the leaderboard screen is loaded
            // normally. The scene can linger additively after the direct in-room
            // return; operating on the persisted (hidden) rows there would call
            // EventSystem.SetSelectedGameObject(null) and steal chat focus.
            if (GhostLinePoller.IsContentSceneActive() || !IsLeaderboardSceneLoaded())
            {
                if (isLeaderboardDragSelecting)
                {
                    EndLeaderboardDragSelection();
                }
                return;
            }
            Transform content = FindLeaderboardScrollViewContent();
            if (content == null || !content.gameObject.activeInHierarchy)
            {
                if (isLeaderboardDragSelecting)
                {
                    EndLeaderboardDragSelection();
                }
                return;
            }

            List<LeaderboardRowItem> rows = leaderboardDragRows;
            rows.Clear();
            int childCount = content.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform child = content.GetChild(i);
                if (child == null || !child.gameObject.activeInHierarchy) continue;

                Toggle toggle = child.GetComponentInChildren<Toggle>(true);
                if (toggle == null || !toggle.gameObject.activeInHierarchy) continue;

                RectTransform rowRect = child as RectTransform;
                RectTransform toggleRect = toggle.GetComponent<RectTransform>();

                rows.Add(new LeaderboardRowItem
                {
                    index = rows.Count,
                    toggle = toggle,
                    rowRect = rowRect,
                    toggleRect = toggleRect
                });
            }

            if (rows.Count == 0)
            {
                if (isLeaderboardDragSelecting)
                {
                    EndLeaderboardDragSelection();
                }
                return;
            }

            for (int i = leaderboardSelectedTogglesQueue.Count - 1; i >= 0; i--)
            {
                Toggle t = leaderboardSelectedTogglesQueue[i];
                if (t == null || !t.gameObject.activeInHierarchy)
                {
                    leaderboardSelectedTogglesQueue.RemoveAt(i);
                }
            }

            foreach (var r in rows)
            {
                if (r.toggle.isOn)
                {
                    if (!leaderboardSelectedTogglesQueue.Contains(r.toggle))
                    {
                        leaderboardSelectedTogglesQueue.Add(r.toggle);
                        if (leaderboardSelectedTogglesQueue.Count > 5)
                        {
                            Toggle oldest = leaderboardSelectedTogglesQueue[0];
                            leaderboardSelectedTogglesQueue.RemoveAt(0);
                            if (oldest != null && oldest.isOn)
                            {
                                oldest.isOn = false;
                            }
                        }
                    }
                }
                else
                {
                    if (leaderboardSelectedTogglesQueue.Contains(r.toggle))
                    {
                        leaderboardSelectedTogglesQueue.Remove(r.toggle);
                    }
                }
            }

            Vector2 mousePos = Input.mousePosition;

            bool IsMouseOverRowToggleRegion(LeaderboardRowItem row)
            {
                if (row.toggleRect == null) return false;
                Canvas canvas = row.toggleRect.GetComponentInParent<Canvas>();
                Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;

                if (RectTransformUtility.RectangleContainsScreenPoint(row.toggleRect, mousePos, cam))
                    return true;

                if (row.rowRect != null)
                {
                    Vector3[] rowCorners = cornerScratchA;
                    row.rowRect.GetWorldCorners(rowCorners);

                    float yMin = Mathf.Min(rowCorners[0].y, rowCorners[2].y);
                    float yMax = Mathf.Max(rowCorners[0].y, rowCorners[2].y);

                    Vector3[] toggleCorners = cornerScratchB;
                    row.toggleRect.GetWorldCorners(toggleCorners);
                    float xToggleCenter = (toggleCorners[0].x + toggleCorners[2].x) * 0.5f;

                    float xMin = xToggleCenter - 50f;
                    float xMax = xToggleCenter + 50f;

                    if (mousePos.x >= xMin && mousePos.x <= xMax && mousePos.y >= yMin && mousePos.y <= yMax)
                        return true;
                }

                return false;
            }

            int GetRowIndexUnderMouse()
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (IsMouseOverRowToggleRegion(rows[i]))
                        return i;
                }
                return -1;
            }

            if (Input.GetMouseButtonDown(0))
            {
                int hitIndex = GetRowIndexUnderMouse();
                if (hitIndex >= 0)
                {
                    isLeaderboardDragSelecting = true;
                    leaderboardDragProcessedToggles.Clear();
                    leaderboardDragStartRowIndex = hitIndex;
                    leaderboardDragLastRowIndex = hitIndex;
                    leaderboardDragStartWasSelected = rows[hitIndex].toggle.isOn;

                    // Drop the EventSystem selection right away so Unity's focus
                    // ring doesn't stick to the started control while dragging.
                    if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
                    {
                        GameObject selected = EventSystem.current.currentSelectedGameObject;
                        if (selected.GetComponentInChildren<Toggle>(true) != null ||
                            selected.GetComponent<Toggle>() != null)
                        {
                            EventSystem.current.SetSelectedGameObject(null);
                        }
                    }

                    if (leaderboardCachedScrollRect != null)
                    {
                        leaderboardScrollRectWasEnabled = leaderboardCachedScrollRect.enabled;
                        leaderboardCachedScrollRect.enabled = false;
                    }

                    // Do NOT pre-mark the start row as processed and do NOT set
                    // isOn here. On a plain click the native Unity Toggle toggles
                    // it once; on a drag the range loop below picks it up.
                }
            }

            if (isLeaderboardDragSelecting && Input.GetMouseButton(0))
            {
                int currentIndex = GetRowIndexUnderMouse();
                if (currentIndex < 0)
                {
                    float bestDist = float.MaxValue;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (rows[i].rowRect != null)
                        {
                            Vector3[] corners = cornerScratchA;
                            rows[i].rowRect.GetWorldCorners(corners);
                            float yMin = Mathf.Min(corners[0].y, corners[2].y);
                            float yMax = Mathf.Max(corners[0].y, corners[2].y);
                            if (mousePos.y >= yMin && mousePos.y <= yMax)
                            {
                                currentIndex = i;
                                break;
                            }
                            float dist = Mathf.Min(Mathf.Abs(mousePos.y - yMin), Mathf.Abs(mousePos.y - yMax));
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                currentIndex = i;
                            }
                        }
                    }
                }

                // Only drag-select when the pointer actually reached a different
                // row. A plain single click (no movement) is handled by the native
                // toggle, so we must not toggling it here (that would double-toggle).
                if (currentIndex != leaderboardDragStartRowIndex)
                {
                    if (currentIndex >= 0 && leaderboardDragLastRowIndex >= 0)
                    {
                        int minR = Mathf.Min(leaderboardDragLastRowIndex, currentIndex);
                        int maxR = Mathf.Max(leaderboardDragLastRowIndex, currentIndex);

                        if (currentIndex >= leaderboardDragLastRowIndex)
                        {
                            for (int r = minR; r <= maxR; r++)
                            {
                                ApplyDragRowToggle(rows[r]);
                            }
                        }
                        else
                        {
                            for (int r = maxR; r >= minR; r--)
                            {
                                ApplyDragRowToggle(rows[r]);
                            }
                        }

                        leaderboardDragLastRowIndex = currentIndex;
                    }
                }
                else if (currentIndex >= 0)
                {
                    leaderboardDragLastRowIndex = currentIndex;
                }
            }

            if (Input.GetMouseButtonUp(0) || !Input.GetMouseButton(0))
            {
                if (isLeaderboardDragSelecting)
                {
                    EndLeaderboardDragSelection();
                }
            }
        }

        private void SelectRowToggle(LeaderboardRowItem row)
        {
            Toggle t = row.toggle;
            if (t == null) return;

            if (leaderboardDragProcessedToggles.Contains(t))
                return;
            leaderboardDragProcessedToggles.Add(t);

            if (!t.isOn)
            {
                if (leaderboardSelectedTogglesQueue.Count >= 5)
                {
                    while (leaderboardSelectedTogglesQueue.Count >= 5)
                    {
                        Toggle oldest = leaderboardSelectedTogglesQueue[0];
                        leaderboardSelectedTogglesQueue.RemoveAt(0);
                        if (oldest != null && oldest.isOn)
                        {
                            oldest.isOn = false;
                        }
                    }
                }

                t.isOn = true;
                if (!leaderboardSelectedTogglesQueue.Contains(t))
                {
                    leaderboardSelectedTogglesQueue.Add(t);
                }
            }
        }

        private void ApplyDragRowToggle(LeaderboardRowItem row)
        {
            if (leaderboardDragStartWasSelected)
                UnselectRowToggle(row);
            else
                SelectRowToggle(row);
        }

        private void UnselectRowToggle(LeaderboardRowItem row)
        {
            Toggle t = row.toggle;
            if (t == null) return;

            if (leaderboardDragProcessedToggles.Contains(t))
                return;
            leaderboardDragProcessedToggles.Add(t);

            if (t.isOn)
            {
                t.isOn = false;
                leaderboardSelectedTogglesQueue.Remove(t);
            }
        }

        private void EndLeaderboardDragSelection()
        {
            isLeaderboardDragSelecting = false;
            leaderboardDragProcessedToggles.Clear();
            leaderboardDragStartRowIndex = -1;
            leaderboardDragLastRowIndex = -1;

            // Drop the EventSystem selection so the Unity focus ring around
            // the last toggled row doesn't stay highlighted.
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
            {
                GameObject selected = EventSystem.current.currentSelectedGameObject;
                if (selected.GetComponentInChildren<Toggle>(true) != null ||
                    selected.GetComponent<Toggle>() != null)
                {
                    EventSystem.current.SetSelectedGameObject(null);
                }
            }

            if (leaderboardCachedScrollRect != null)
            {
                leaderboardCachedScrollRect.enabled = leaderboardScrollRectWasEnabled;
            }
        }

        private class LeaderboardSearchInputRelay : MonoBehaviour, IPointerClickHandler
        {
            public GhostLinePoller owner;

            public void OnPointerClick(PointerEventData eventData)
            {
                if (owner != null)
                    owner.BeginLeaderboardSearchInput();
            }
        }

        private void RefocusLeaderboardSearchInput()
        {
            if (!leaderboardSearchRefocusPending || leaderboardSearchInput == null)
                return;
            // Don't steal focus from the game's chat / another text field.
            if (IsChatTyping())
            {
                leaderboardSearchRefocusPending = false;
                return;
            }

            leaderboardSearchRefocusPending = false;
            if (!leaderboardSearchInput.isActiveAndEnabled)
                return;

            if (EventSystem.current != null &&
                EventSystem.current.currentSelectedGameObject != leaderboardSearchInput.gameObject)
            {
                EventSystem.current.SetSelectedGameObject(leaderboardSearchInput.gameObject);
            }

            leaderboardSearchInput.ActivateInputField();
            ScheduleLeaderboardSearchSelectionCorrection();
        }

        private void ScheduleLeaderboardSearchSelectionCorrection()
        {
            if (leaderboardSearchRefocusCoroutine != null)
                StopCoroutine(leaderboardSearchRefocusCoroutine);
            leaderboardSearchRefocusCoroutine = FinishLeaderboardSearchRefocus();
            StartCoroutine(leaderboardSearchRefocusCoroutine);
        }

        private System.Collections.IEnumerator FinishLeaderboardSearchRefocus()
        {
            yield return new WaitForEndOfFrame();

            if (leaderboardSearchInput != null && leaderboardSearchInput.isActiveAndEnabled)
            {
                leaderboardSearchInput.MoveTextEnd(false);
                int end = leaderboardSearchInput.text != null ? leaderboardSearchInput.text.Length : 0;
                leaderboardSearchInput.caretPosition = end;
                leaderboardSearchInput.selectionAnchorPosition = end;
                leaderboardSearchInput.selectionFocusPosition = end;
            }

            leaderboardSearchRefocusCoroutine = null;
        }

        private void MaintainLeaderboardSearchFocus()
        {
            if (!leaderboardSearchFocusLock || leaderboardSearchInput == null || EventSystem.current == null)
                return;
            // Never fight the game's chat field for focus.
            if (IsChatTyping())
                return;

            GameObject popup;
            System.Collections.IList items;
            if (!TryGetLeaderboardPopup(out popup, out items))
                return;

            // Each DropdownItem selects itself on pointer enter, and the lines
            // below take the selection back, so moving the mouse over the list
            // flipped focus (and re-ran the caret fix-up) every frame: visible
            // stutter. Disabled items no longer get pointer events; the Toggle
            // still highlights on hover and picks the option on click.
            for (int i = 0; i < items.Count; i++)
            {
                Behaviour item = items[i] as Behaviour;
                if (item != null && item.enabled)
                    item.enabled = false;
            }

            if (EventSystem.current.currentSelectedGameObject == leaderboardSearchInput.gameObject)
                return;

            EventSystem.current.SetSelectedGameObject(leaderboardSearchInput.gameObject);
            ScheduleLeaderboardSearchSelectionCorrection();
        }
    }
}
