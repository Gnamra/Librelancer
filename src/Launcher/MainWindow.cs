﻿using System;
using System.IO;
using Eto.Forms;
using Eto.Drawing;
namespace Launcher
{
	public class MainWindow : Form
	{
		TextBox textInput;

		public MainWindow ()
		{
			Title = "Librelancer Launcher";
			ClientSize = new Size (400, 100);
			Resizable = false;
			var layout = new TableLayout ();
			layout.Spacing = new Size (2, 2);
			layout.Padding = new Padding (5, 5, 5, 5);

			layout.Rows.Add (new TableRow (
				new Label { Text = "Freelancer Directory:" }
			));

			textInput = new TextBox ();
			var findButton = new Button () { Text = "..." };
			findButton.Click += FindButton_Click;

			layout.Rows.Add (new TableRow (
				new TableCell(textInput, true),
				findButton
			));
			var launchButton = new Button () { Text = "Launch Librelancer" };
			launchButton.Click += LaunchButton_Click;
			layout.Rows.Add(new TableRow { ScaleHeight = true });

			layout.Rows.Add (
				new TableRow (
				TableLayout.AutoSized (launchButton, null, true)
				)
			);
			Content = layout;
		}

		void FindButton_Click (object sender, EventArgs e)
		{
			var dlg = new SelectFolderDialog ();
			if (dlg.ShowDialog (this) == DialogResult.Ok) {
				textInput.Text = dlg.Directory;
			}
		}

		void LaunchButton_Click (object sender, EventArgs e)
		{
			if (Directory.Exists(textInput.Text))
			{
				if(!Directory.Exists(Path.Combine(textInput.Text, "DATA"))
					|| !Directory.Exists(Path.Combine(textInput.Text, "EXE")))
				{
					MessageBox.Show (this, "Not a valid freelancer directory", "Librelancer", MessageBoxType.Error);
					return;
				}
				Program.LaunchPath = textInput.Text;
				Close ();			
			}
			else
			{
				MessageBox.Show(this, "Path does not exist", "Librelancer", MessageBoxType.Error);
			}
		}
	}
}
