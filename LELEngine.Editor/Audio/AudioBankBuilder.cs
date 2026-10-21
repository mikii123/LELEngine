using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Audio;

namespace LELEngine.Editor.Audio
{
	/// <summary>
	///     Writes the bank files of an audio project (game builds: Data/Audio/Banks; the Audio Studio's Build Banks:
	///     Library/AudioBanks). Every bank of the project is written; stale bank files in the folder are removed first.
	/// </summary>
	internal static class AudioBankBuilder
	{
		#region PublicMethods

		/// <summary>Builds every bank into <paramref name="folder" />; one log line per bank; false when a bank failed.</summary>
		public static bool BuildAll(AudioProject project, string folder, Action<string> log)
		{
			if (project == null)
			{
				log?.Invoke("No audio project: no banks.");
				return true;
			}

			Directory.CreateDirectory(folder);
			foreach (string stale in Directory.GetFiles(folder, "*" + AudioBankFile.Extension))
			{
				try
				{
					File.Delete(stale);
				}
				catch (Exception e)
				{
					log?.Invoke("Warning: cannot remove the old bank " + Path.GetFileName(stale) + " (" + e.Message + ").");
				}
			}

			bool ok = true;
			var warnings = new List<string>();
			foreach (AudioBank bank in project.Banks)
			{
				string file = Path.Combine(folder, SafeFileName(bank.Name) + AudioBankFile.Extension);
				try
				{
					string summary = AudioBankCompiler.WriteFile(project, bank, file, warnings.Add);
					log?.Invoke("Bank " + summary);
				}
				catch (Exception e)
				{
					ok = false;
					log?.Invoke("Error: bank " + bank.Name + " could not be written (" + e.Message + ").");
				}
			}

			foreach (string warning in warnings)
			{
				log?.Invoke("Warning: " + warning);
			}

			// The master bank must exist for the player to find the others.
			if (project.FindBankByName(AudioProject.MasterBankName) == null)
			{
				log?.Invoke("Warning: the audio project has no Master bank; the player loads nothing at start.");
			}

			return ok;
		}

		/// <summary>The audio project of the game project (project settings, else the only .lelaudio file), or null.</summary>
		public static AudioProject FindProject()
		{
			string path = AudioProject.FindProjectPath();
			return path != null ? AudioProject.Load(path) : null;
		}

		#endregion

		#region PrivateMethods

		private static string SafeFileName(string name)
		{
			var builder = new System.Text.StringBuilder();
			foreach (char c in name)
			{
				builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
			}

			return builder.Length > 0 ? builder.ToString() : "Bank";
		}

		#endregion
	}
}
