namespace LELEngine
{
	/// <summary>
	///     Stands in for a component whose script type could not be found when the scene was loaded (renamed,
	///     deleted, or the game assembly failed to compile). It keeps the component's data and writes it back
	///     unchanged when the scene is saved, so nothing is lost once the script exists again.
	/// </summary>
	public sealed class MissingComponent : Behaviour
	{
		#region PublicFields

		/// <summary>Type name stored in the scene file.</summary>
		public string TypeName { get; internal set; }

		#endregion

		#region InternalFields

		/// <summary>The component's JSON entry as it was read.</summary>
		internal string RawJson;

		#endregion
	}
}
