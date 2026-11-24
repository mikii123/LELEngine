using System.Text;

namespace LELEngine.Graphics.Vulkan
{
	internal sealed partial class SpirvModule
	{
		/// <summary>
		///     Adds a note of how the module was processed (OpModuleProcessed, a debug instruction: nothing runs
		///     differently). A driver that keeps compiled shaders by their words keeps a module with a note apart from the
		///     same module without one.
		/// </summary>
		public void AddProcessedNote(string text)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(text);
			int words = bytes.Length / 4 + 1;
			uint[] instruction = new uint[1 + words];
			instruction[0] = (uint)instruction.Length << 16 | Op.ModuleProcessed;
			for (int i = 0; i < bytes.Length; i++)
			{
				instruction[1 + i / 4] |= (uint)bytes[i] << (i % 4 * 8);
			}

			// Debug instructions end with the module-processed notes, before the annotations.
			instructions.Insert(annotationStart, instruction);
			annotationStart++;
			annotationEnd++;
		}
	}
}
