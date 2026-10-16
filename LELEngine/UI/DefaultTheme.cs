namespace LELEngine.UIElements
{
	/// <summary>
	///     The built-in runtime theme: the look of every control (dark, flat) through their USS classes. Panels start
	///     from it unless <see cref="PanelSettings.useDefaultTheme" /> is off; a project theme sheet overrides it.
	/// </summary>
	public static class DefaultTheme
	{
		#region PublicFields

		public static StyleSheet StyleSheet => sheet ??= StyleSheet.Parse(Source, null, "DefaultTheme");

		public const string Source = @"
:root {
	--unity-colors-default-background: #2b2b2b;
	--unity-colors-default-border: #1a1a1a;
	--unity-colors-default-text: #e8e8e8;
	--unity-colors-highlight-background: #3e7bd6;
	--unity-colors-highlight-background-hover: #4d8be6;
	--unity-colors-button-background: #4a4a4a;
	--unity-colors-button-background-hover: #5a5a5a;
	--unity-colors-button-background-pressed: #2f6fc8;
	--unity-colors-input-background: #1f1f1f;
	--unity-colors-input-border: #3a3a3a;
	--unity-colors-input-border-focus: #3e7bd6;
	--unity-colors-scrollbar-track: #262626;
	--unity-colors-scrollbar-thumb: #5c5c5c;
	--unity-colors-scrollbar-thumb-hover: #7a7a7a;
	--unity-metrics-default-border_radius: 4px;
	color: #e8e8e8;
	font-size: 14px;
}

.unity-text-element {
	color: var(--unity-colors-default-text);
}

.unity-label {
	padding: 2px 4px;
	white-space: nowrap;
}

.unity-disabled {
	opacity: 0.5;
}

/* ---- Button ---- */
.unity-button {
	background-color: var(--unity-colors-button-background);
	border-color: var(--unity-colors-default-border);
	border-width: 1px;
	border-radius: var(--unity-metrics-default-border_radius);
	padding: 4px 12px;
	margin: 2px;
	min-height: 26px;
	-unity-text-align: middle-center;
	white-space: nowrap;
	transition: background-color 0.08s ease-out;
	cursor: link;
}

.unity-button:hover {
	background-color: var(--unity-colors-button-background-hover);
}

.unity-button:active {
	background-color: var(--unity-colors-button-background-pressed);
}

.unity-button:focus {
	border-color: var(--unity-colors-input-border-focus);
}

/* ---- Fields ---- */
.unity-base-field {
	flex-direction: row;
	align-items: center;
	margin: 2px;
	min-height: 24px;
}

.unity-base-field__label {
	min-width: 120px;
	padding-right: 6px;
	-unity-text-align: middle-left;
	overflow: hidden;
	text-overflow: ellipsis;
}

.unity-base-field__input {
	flex-grow: 1;
	flex-basis: 0;
}

.unity-base-field--no-label > .unity-base-field__label {
	display: none;
}

.unity-base-text-field__input {
	background-color: var(--unity-colors-input-background);
	border-color: var(--unity-colors-input-border);
	border-width: 1px;
	border-radius: var(--unity-metrics-default-border_radius);
	padding: 2px 6px;
	min-height: 24px;
	flex-direction: row;
	align-items: center;
	overflow: hidden;
	cursor: text;
	transition: border-color 0.1s;
}

.unity-base-text-field__input:hover {
	border-color: #555555;
}

.unity-base-text-field:focus .unity-base-text-field__input,
.unity-base-text-field__input:focus {
	border-color: var(--unity-colors-input-border-focus);
}

.unity-text-field__input--multiline {
	align-items: flex-start;
	min-height: 60px;
}

.unity-base-text-field__input .unity-text-element {
	padding: 0;
	flex-grow: 1;
	white-space: nowrap;
	overflow: hidden;
}

.unity-text-field__input--multiline .unity-text-element {
	white-space: normal;
}

.unity-base-text-field__placeholder {
	color: #808080;
}

/* ---- Toggle ---- */
.unity-toggle {
	cursor: link;
}

.unity-toggle__input {
	flex-direction: row;
	align-items: center;
	flex-grow: 0;
	flex-shrink: 0;
	flex-basis: auto;
}

.unity-toggle__checkmark {
	width: 18px;
	height: 18px;
	background-color: var(--unity-colors-input-background);
	border-color: var(--unity-colors-input-border);
	border-width: 1px;
	border-radius: 3px;
	margin-right: 6px;
	transition: background-color 0.1s, border-color 0.1s;
}

.unity-toggle:hover .unity-toggle__checkmark {
	border-color: #777777;
}

.unity-toggle:checked .unity-toggle__checkmark {
	background-color: var(--unity-colors-highlight-background);
	border-color: var(--unity-colors-highlight-background);
}

.unity-toggle:checked .unity-toggle__checkmark > .unity-toggle__checkmark-tick {
	display: flex;
}

.unity-toggle__checkmark-tick {
	display: none;
	position: absolute;
	left: 5px;
	top: 1px;
	width: 6px;
	height: 11px;
	border-right-width: 2px;
	border-bottom-width: 2px;
	border-color: white;
	rotate: 45deg;
}

.unity-toggle__text {
	padding-left: 2px;
}

/* ---- Radio ---- */
.unity-radio-button {
	cursor: link;
}

.unity-radio-button__input {
	flex-direction: row;
	align-items: center;
	flex-grow: 0;
	flex-shrink: 0;
	flex-basis: auto;
}

.unity-radio-button__checkmark-background {
	width: 18px;
	height: 18px;
	border-radius: 9px;
	background-color: var(--unity-colors-input-background);
	border-color: var(--unity-colors-input-border);
	border-width: 1px;
	margin-right: 6px;
	align-items: center;
	justify-content: center;
}

.unity-radio-button__checkmark {
	width: 8px;
	height: 8px;
	border-radius: 4px;
	background-color: var(--unity-colors-highlight-background);
	display: none;
}

.unity-radio-button:checked .unity-radio-button__checkmark {
	display: flex;
}

.unity-radio-button:checked .unity-radio-button__checkmark-background {
	border-color: var(--unity-colors-highlight-background);
}

.unity-radio-button-group__container {
	flex-direction: column;
}

/* ---- Slider ---- */
.unity-base-slider__input {
	flex-direction: row;
	align-items: center;
	min-height: 24px;
}

.unity-base-slider--vertical {
	flex-direction: column;
	align-items: stretch;
}

.unity-base-slider--vertical > .unity-base-field__input {
	flex-direction: column;
	flex-grow: 1;
	min-height: 120px;
	min-width: 24px;
}

.unity-base-slider__drag-container {
	flex-grow: 1;
	justify-content: center;
	min-height: 24px;
	cursor: link;
}

.unity-base-slider--vertical .unity-base-slider__drag-container {
	align-items: center;
	justify-content: flex-start;
	min-width: 24px;
	min-height: 0;
	flex-grow: 1;
}

.unity-base-slider__tracker {
	height: 4px;
	background-color: var(--unity-colors-input-background);
	border-color: var(--unity-colors-input-border);
	border-width: 1px;
	border-radius: 2px;
}

.unity-base-slider--vertical .unity-base-slider__tracker {
	width: 4px;
	height: auto;
	flex-grow: 1;
}

.unity-base-slider__fill {
	position: absolute;
	height: 4px;
	background-color: var(--unity-colors-highlight-background);
	border-radius: 2px;
}

.unity-base-slider__dragger {
	position: absolute;
	width: 14px;
	height: 14px;
	border-radius: 7px;
	background-color: #d0d0d0;
	border-color: #808080;
	border-width: 1px;
	transition: background-color 0.1s, scale 0.1s;
}

.unity-base-slider__dragger:hover {
	background-color: #ffffff;
	scale: 1.1;
}

.unity-base-slider__dragger-border {
	position: absolute;
}

.unity-base-slider__text-field {
	width: 56px;
	margin-left: 6px;
	flex-grow: 0;
}

.unity-min-max-slider__dragger {
	position: absolute;
	height: 14px;
	border-radius: 4px;
	background-color: var(--unity-colors-highlight-background);
	border-color: #808080;
	border-width: 1px;
}

.unity-min-max-slider__min-thumb,
.unity-min-max-slider__max-thumb {
	position: absolute;
	width: 8px;
	height: 14px;
	background-color: #e0e0e0;
	border-radius: 3px;
}

/* ---- Progress bar ---- */
.unity-progress-bar {
	min-height: 20px;
	margin: 2px;
}

.unity-progress-bar__container {
	flex-grow: 1;
	min-height: 20px;
}

.unity-progress-bar__background {
	flex-grow: 1;
	background-color: var(--unity-colors-input-background);
	border-color: var(--unity-colors-input-border);
	border-width: 1px;
	border-radius: 3px;
	overflow: hidden;
}

.unity-progress-bar__progress {
	background-color: var(--unity-colors-highlight-background);
	flex-grow: 0;
	min-height: 18px;
}

.unity-progress-bar__title-container {
	position: absolute;
	left: 0;
	right: 0;
	top: 0;
	bottom: 0;
	justify-content: center;
	align-items: center;
}

.unity-progress-bar__title {
	-unity-text-align: middle-center;
	font-size: 12px;
}

/* ---- Scroll view ---- */
.unity-scroll-view {
	flex-grow: 1;
	overflow: hidden;
}

.unity-scroll-view__content-and-vertical-scroll-container {
	flex-direction: row;
	flex-grow: 1;
}

.unity-scroll-view__content-viewport {
	flex-grow: 1;
	overflow: hidden;
}

.unity-scroll-view__content-container {
	flex-shrink: 0;
}

.unity-scroll-view--horizontal .unity-scroll-view__content-container {
	flex-direction: row;
}

.unity-scroller {
	flex-shrink: 0;
}

.unity-scroller--vertical {
	width: 14px;
	flex-direction: column;
}

.unity-scroller--horizontal {
	height: 14px;
	flex-direction: row;
}

.unity-scroller__slider {
	flex-grow: 1;
	margin: 0;
	min-height: 0;
	min-width: 0;
}

.unity-scroller__slider > .unity-base-field__input {
	min-height: 0;
	min-width: 0;
}

.unity-scroller__slider .unity-base-slider__drag-container {
	min-height: 0;
	min-width: 0;
	background-color: var(--unity-colors-scrollbar-track);
}

.unity-scroller__slider .unity-base-slider__tracker {
	display: none;
}

.unity-scroller__slider .unity-base-slider__dragger {
	border-radius: 5px;
	background-color: var(--unity-colors-scrollbar-thumb);
	border-width: 0;
	scale: 1;
}

.unity-scroller__slider .unity-base-slider__dragger:hover {
	background-color: var(--unity-colors-scrollbar-thumb-hover);
	scale: 1;
}

.unity-scroller--vertical .unity-base-slider__dragger {
	width: 10px;
	left: 2px;
}

.unity-scroller--horizontal .unity-base-slider__dragger {
	height: 10px;
	top: 2px;
}

.unity-scroller__low-button,
.unity-scroller__high-button {
	width: 14px;
	height: 14px;
	margin: 0;
	padding: 0;
	min-height: 0;
	border-width: 0;
	border-radius: 0;
	background-color: var(--unity-colors-scrollbar-track);
	display: none;
}

/* ---- Foldout ---- */
.unity-foldout {
	margin: 2px;
}

.unity-foldout__toggle {
	margin: 0;
	min-height: 22px;
}

.unity-foldout__toggle > .unity-toggle__input {
	flex-direction: row;
	align-items: center;
}

.unity-foldout__toggle .unity-toggle__checkmark {
	width: 0;
	height: 0;
	background-color: transparent;
	border-width: 0;
	margin-right: 14px;
}

.unity-foldout .unity-foldout__toggle:checked .unity-toggle__checkmark > .unity-toggle__checkmark-tick,
.unity-foldout__toggle .unity-toggle__checkmark-tick {
	display: none;
}

.unity-foldout__arrow {
	position: absolute;
	left: 2px;
	top: 4px;
	width: 8px;
	height: 8px;
	border-right-width: 2px;
	border-bottom-width: 2px;
	border-color: #c0c0c0;
	rotate: -45deg;
	transition: rotate 0.12s ease-out;
}

.unity-foldout--open > .unity-foldout__toggle .unity-foldout__arrow {
	rotate: 45deg;
}

.unity-foldout__content {
	margin-left: 18px;
}

/* ---- Dropdown ---- */
.unity-base-popup-field__input {
	background-color: var(--unity-colors-button-background);
	border-color: var(--unity-colors-default-border);
	border-width: 1px;
	border-radius: var(--unity-metrics-default-border_radius);
	padding: 2px 8px;
	min-height: 24px;
	flex-direction: row;
	align-items: center;
	cursor: link;
}

.unity-base-popup-field__input:hover {
	background-color: var(--unity-colors-button-background-hover);
}

.unity-base-popup-field__text {
	flex-grow: 1;
	padding: 0;
	overflow: hidden;
	text-overflow: ellipsis;
}

.unity-base-popup-field__arrow {
	width: 8px;
	height: 8px;
	border-right-width: 2px;
	border-bottom-width: 2px;
	border-color: #c0c0c0;
	rotate: 45deg;
	margin-left: 6px;
	margin-top: -4px;
}

.unity-base-dropdown__container-outer {
	position: absolute;
	background-color: #333333;
	border-color: #111111;
	border-width: 1px;
	border-radius: 4px;
	padding: 2px;
	min-width: 80px;
}

.unity-base-dropdown__item {
	padding: 4px 10px;
	min-height: 22px;
	-unity-text-align: middle-left;
	border-radius: 3px;
	cursor: link;
}

.unity-base-dropdown__item:hover {
	background-color: var(--unity-colors-highlight-background);
}

.unity-base-dropdown__item--checked {
	background-color: #444444;
}

/* ---- List view ---- */
.unity-list-view {
	flex-grow: 1;
	overflow: hidden;
	background-color: var(--unity-colors-input-background);
	border-color: var(--unity-colors-input-border);
	border-width: 1px;
}

.unity-list-view__item {
	flex-direction: row;
	align-items: center;
	padding-left: 6px;
}

.unity-list-view__item:hover {
	background-color: #353535;
}

.unity-list-view__item--selected {
	background-color: var(--unity-colors-highlight-background);
}

.unity-list-view__item--selected:hover {
	background-color: var(--unity-colors-highlight-background-hover);
}

.unity-list-view__empty-label {
	padding: 8px;
	color: #888888;
	-unity-text-align: middle-center;
}

/* ---- Group box, box, help box ---- */
.unity-group-box {
	border-color: #3a3a3a;
	border-width: 1px;
	border-radius: 4px;
	padding: 6px;
	margin: 4px;
}

.unity-group-box__label {
	font-size: 13px;
	margin-bottom: 4px;
	-unity-font-style: bold;
}

.unity-box {
	background-color: #353535;
	border-color: #1f1f1f;
	border-width: 1px;
	border-radius: 3px;
	padding: 4px;
	margin: 2px;
}

.unity-help-box {
	flex-direction: row;
	align-items: center;
	background-color: #353535;
	border-color: #1f1f1f;
	border-width: 1px;
	border-radius: 3px;
	padding: 6px;
	margin: 2px;
}

.unity-help-box__icon {
	width: 16px;
	height: 16px;
	border-radius: 8px;
	margin-right: 8px;
	background-color: #808080;
}

.unity-help-box__icon--info { background-color: #3e7bd6; }
.unity-help-box__icon--warning { background-color: #e0a030; }
.unity-help-box__icon--error { background-color: #d04040; }

.unity-help-box__label {
	white-space: normal;
	flex-grow: 1;
	flex-shrink: 1;
}

/* ---- Composite fields ---- */
.unity-composite-field__input {
	flex-direction: row;
}

.unity-composite-field__field {
	flex-grow: 1;
	flex-basis: 0;
	margin: 0 2px;
}

.unity-composite-field__field .unity-base-field__label {
	min-width: 14px;
	padding-right: 2px;
}

/* ---- Tooltip ---- */
.unity-tooltip {
	position: absolute;
	background-color: #1a1a1a;
	border-color: #000000;
	border-width: 1px;
	border-radius: 3px;
	padding: 4px 8px;
	color: #f0f0f0;
	font-size: 12px;
	max-width: 300px;
	white-space: normal;
}
";

		#endregion

		#region PrivateFields

		private static StyleSheet sheet;

		#endregion
	}
}
