using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace EdgeAlignInspect
{
	public class Form1 : Form
	{
		private sealed class BaseBindingOption
		{
			public ReferenceBaseKind Kind;

			public int Index;

			public string Id;

			public string Name;
		}

		private struct RigidEditTransform
		{
			public double RefX;

			public double RefY;

			public double ImageX;

			public double ImageY;

			public double Angle;

			public static RigidEditTransform Identity => new RigidEditTransform();

			public bool IsIdentity => Math.Abs(RefX - ImageX) < 1E-06 && Math.Abs(RefY - ImageY) < 1E-06 && Math.Abs(Angle) < 1E-08;

			public PointF ToImage(PointF point)
			{
				if (float.IsNaN(point.X) || float.IsNaN(point.Y))
				{
					return point;
				}
				double cos = Math.Cos(Angle);
				double sin = Math.Sin(Angle);
				double dx = point.X - RefX;
				double dy = point.Y - RefY;
				return new PointF((float)(ImageX + cos * dx - sin * dy), (float)(ImageY + sin * dx + cos * dy));
			}

			public PointF ToReference(PointF point)
			{
				if (float.IsNaN(point.X) || float.IsNaN(point.Y))
				{
					return point;
				}
				double cos = Math.Cos(Angle);
				double sin = Math.Sin(Angle);
				double dx = point.X - ImageX;
				double dy = point.Y - ImageY;
				return new PointF((float)(RefX + cos * dx + sin * dy), (float)(RefY - sin * dx + cos * dy));
			}

			public RotRectF ToImage(RotRectF roi)
			{
				return roi.IsEmpty ? roi : new RotRectF(ToImage(roi.Center), RotRectF.NormalizeAngle(roi.AngleRad + (float)Angle), roi.HalfLen1, roi.HalfLen2);
			}

			public RotRectF ToReference(RotRectF roi)
			{
				return roi.IsEmpty ? roi : new RotRectF(ToReference(roi.Center), RotRectF.NormalizeAngle(roi.AngleRad - (float)Angle), roi.HalfLen1, roi.HalfLen2);
			}

			public CircleRoiF ToImage(CircleRoiF roi)
			{
				return roi.IsEmpty ? roi : new CircleRoiF(ToImage(roi.Center), roi.Radius);
			}

			public CircleRoiF ToReference(CircleRoiF roi)
			{
				return roi.IsEmpty ? roi : new CircleRoiF(ToReference(roi.Center), roi.Radius);
			}
		}

		private const int KeepRightPanelWidth = 510;

		private Bitmap _src;

		private EdgeInspectJob _job = new EdgeInspectJob();

		private EdgeInspectJob _savedJob;

		private RotRectF _lastTemplateRoi = default(RotRectF);

		private readonly TemplateEdgeInspectProcessor _proc = new TemplateEdgeInspectProcessor();

		private bool _resultValid = false;

		private bool _suppressRoiChanged = false;

		private bool _suppressUiEvents = false;

		private bool _pendingImportedDisplayRefresh = false;

		private bool _hasUnsavedChanges = false;

		private RigidEditTransform _editTransform = RigidEditTransform.Identity;

		private bool _editTransformResolved = true;

		private bool _templateModelDirty = false;

		private readonly List<BaseBindingOption> _baseBindingOptions = new List<BaseBindingOption>();

		private IContainer components = null;

		private SplitContainer splitMain;

		private HalconCanvas canvas;

		private Panel rightPanel;

		private GroupBox grpOps;

		private Button btnLoad;

		private Button btnAddBaseRoi;

		private Button btnAddCircleBaseRoi;

		private Button btnAddCirclePointRoi;

		private Button btnAddDetectRoi;

		private Button btnDeleteRoi;

		private Button btnTemplateSettings;

		private Button btnSaveTeach;

		private Button btnRun;

		private Button btnConfirm;

		private Label lblRoiHint;

		private GroupBox grpParams;

		private CheckBox chkDetectEnabled;

		private NumericUpDown numNominal;

		private ComboBox cboAngleRef;

		private ComboBox cboDetectBindBase;

		private ComboBox cboDetectMode;

		private NumericUpDown numConsecutiveAbnormalPoints;

		private CheckBox chkUseReferenceLine;

		private ComboBox cboBasePol;

		private ComboBox cboDetPol;

		private NumericUpDown numBaseMeasures;

		private NumericUpDown numBaseSigma;

		private NumericUpDown numBaseTh;

		private NumericUpDown numBaseOutward;

		private NumericUpDown numDetMeasures;

		private NumericUpDown numDetSigma;

		private NumericUpDown numDetTh;

		private NumericUpDown numDetFitRatio;

		private CheckBox chkEnableMatch;

		private CheckBox chkEnableSearchRoi;

		private ComboBox cboLanguage;

		private NumericUpDown numMatchMinScore;

		private NumericUpDown numMatchAngleStart;

		private NumericUpDown numMatchAngleExtent;

		private Panel cardResult;

		private Label lblResultTitle;

		private Label lblStatus;

		private Label lblSummary;

		private Label lblResultTip;

		public EdgeInspectJob ReturnedJob { get; private set; }

		public Bitmap LastResultImage { get; private set; }

		public Form1()
		{
			InitializeComponent();
			canvas.ShowTemplateRoi = false;
			canvas.ShowTemplateSearchRoi = false;
			lblRoiHint.Text = string.Empty;
			lblRoiHint.Visible = false;
			lblRoiHint.Height = 0;
			_job.Normalize();
			_job.Match.Enabled = false;
			chkEnableMatch.Checked = false;
			SetLanguageComboSelection(_job.Language);
			BindEvents();
			ApplyLanguageToUi();
			UpdateMatchUiState();
			UpdateUseReferenceUiState();
			UpdateSelectionHint();
			LoadSelectedDetectToUi();
			LoadCurrentBaseCaliperToUi();
			LoadCurrentDetectCaliperToUi();
			SetReady("READY", "流程：加载图片 → 按需启用模板匹配/添加ROI → 点击左侧ROI分别配置参数 → 保存(示教) → 运行");
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			InitSplitMain();
			LayoutRightPanelOnePage();
			FixRightPanelLayout();
			RequestImportedDisplayRefresh();
		}

		protected override void OnResize(EventArgs e)
		{
			base.OnResize(e);
			FixRightWidth();
			LayoutRightPanelOnePage();
			FixRightPanelLayout();
		}

		private void InitSplitMain()
		{
			splitMain.Panel1MinSize = 320;
			splitMain.Panel2MinSize = 500;
			FixRightWidth();
		}

		public void SetImage(Bitmap bmp)
		{
			if (bmp == null)
			{
				return;
			}
			_src?.Dispose();
			_src = new Bitmap(bmp);
			canvas.LoadBitmap(_src);
			canvas.ShowRois = true;
			canvas.ShowRuntimeRois = false;
			canvas.ClearRuntimeRois();
			_suppressRoiChanged = true;
			try
			{
				_job.Normalize();
				if (_job.Match.Enabled && _job.TemplateRoi.IsEmpty)
				{
					_job.TemplateRoi = CreateDefaultTemplateRoi();
				}
				if (_job.Match.Enabled && _job.TemplateSearchRoi.IsEmpty && !_job.TemplateRoi.IsEmpty)
				{
					_job.TemplateSearchRoi = CreateDefaultTemplateSearchRoi(_job.TemplateRoi);
				}
				ResolveEditTransformForCurrentImage();
				SetCanvasRoisFromJob();
			}
			finally
			{
				_suppressRoiChanged = false;
			}
			SelectInitialCanvasTarget();
			_resultValid = false;
			SetReady(_editTransformResolved ? "READY" : "模板未匹配", _editTransformResolved ? (_job.Match.Enabled ? "图片已加载，ROI 已按当前模板姿态显示。" : "图片已加载：模板匹配当前为关闭状态。") : "当前图像未找到模板，已锁定 ROI 编辑以保护模板参考坐标。");
			RequestImportedDisplayRefresh();
		}

		public void SetJob(EdgeInspectJob job)
		{
			if (job == null)
			{
				return;
			}
			_job = job.DeepClone() ?? new EdgeInspectJob();
			_job.Normalize();
			NormalizeTemplateTransformFlags(_job);
			if (_src != null && _job.Match.Enabled && _job.TemplateRoi.IsEmpty)
			{
				_job.TemplateRoi = CreateDefaultTemplateRoi();
			}
			if (_src != null && _job.Match.Enabled && _job.TemplateSearchRoi.IsEmpty && !_job.TemplateRoi.IsEmpty)
			{
				_job.TemplateSearchRoi = CreateDefaultTemplateSearchRoi(_job.TemplateRoi);
			}
			_templateModelDirty = _job.Match.Enabled && !HasValidTeachData(_job);
			ResolveEditTransformForCurrentImage();
			_savedJob = _job.DeepClone();
			_hasUnsavedChanges = false;
			bool previousSuppressUiEvents = _suppressUiEvents;
			bool previousSuppressRoiChanged = _suppressRoiChanged;
			_suppressUiEvents = true;
			_suppressRoiChanged = true;
			try
			{
				LoadJobToUi(_job);
				SetCanvasRoisFromJob();
				canvas.ShowRois = true;
				canvas.ShowRuntimeRois = false;
				canvas.ClearRuntimeRois();
				SelectInitialCanvasTarget();
				RefreshBaseBindingCombo();
				LoadSelectedDetectToUi();
				LoadCurrentBaseCaliperToUi();
				LoadCurrentDetectCaliperToUi();
				UpdateSelectionHint();
			}
			finally
			{
				_suppressUiEvents = previousSuppressUiEvents;
				_suppressRoiChanged = previousSuppressRoiChanged;
			}
			_resultValid = false;
			SetReady(_editTransformResolved ? "READY" : "模板未匹配", _editTransformResolved ? "参数已回显；ROI 已按当前图像中的模板姿态显示。" : "当前图像未找到模板，已锁定 ROI 编辑以避免把当前坐标误写为模板参考坐标。");
			RequestImportedDisplayRefresh();
		}

		public EdgeInspectJob GetJob()
		{
			if (ReturnedJob != null)
			{
				return ReturnedJob.DeepClone();
			}
			if (_savedJob != null)
			{
				return _savedJob.DeepClone();
			}
			return _job?.DeepClone();
		}

		public EdgeInspectResult RunInspectionForSdk(EdgeInspectJob job, double acceptedTolerance, double pixelResolutionX, double pixelResolutionY, bool returnResultImage)
		{
			return RunInspectionForSdk(job, new EdgeInspectionToleranceOptions
			{
				BurrToleranceMm = acceptedTolerance,
				DentToleranceMm = acceptedTolerance,
				OverEdgeToleranceMm = acceptedTolerance,
				CopperLeakToleranceMm = acceptedTolerance,
				PixelResolutionX = pixelResolutionX,
				PixelResolutionY = pixelResolutionY
			}, returnResultImage);
		}

		public EdgeInspectResult RunInspectionForSdk(EdgeInspectJob job, EdgeInspectionToleranceOptions options, bool returnResultImage)
		{
			if (_src == null)
			{
				InspectionLanguage language = (job ?? _job)?.Language ?? InspectionLanguage.Chinese;
				return new EdgeInspectResult
				{
					Success = false,
					NgReasons = NgReason.ParameterInvalid,
					Language = language,
					Message = LocalizedText.Message("输入图像为空。", language)
				};
			}
			if (options == null)
			{
				InspectionLanguage language = (job ?? _job)?.Language ?? InspectionLanguage.Chinese;
				return new EdgeInspectResult
				{
					Success = false,
					NgReasons = NgReason.ParameterInvalid,
					Language = language,
					Message = LocalizedText.Message("公差参数为空。", language)
				};
			}
			EdgeInspectJob edgeInspectJob = (job ?? _job)?.DeepClone() ?? new EdgeInspectJob();
			edgeInspectJob.UseExternalBurrTolerance = true;
			edgeInspectJob.ExternalBurrTolerance = options.BurrToleranceMm;
			edgeInspectJob.ExternalDentTolerance = options.DentToleranceMm;
			edgeInspectJob.ExternalOverEdgeTolerance = options.OverEdgeToleranceMm;
			edgeInspectJob.ExternalCopperLeakTolerance = options.CopperLeakToleranceMm;
			edgeInspectJob.PixelResolutionX = options.PixelResolutionX;
			edgeInspectJob.PixelResolutionY = options.PixelResolutionY;
			edgeInspectJob.Normalize();
			EdgeInspectResult edgeInspectResult = _proc.Inspect(_src, edgeInspectJob);
			ShowResult(edgeInspectResult);
			LastResultImage?.Dispose();
			LastResultImage = null;
			if (returnResultImage)
			{
				PrepareSdkResultViewport();
				LastResultImage = CaptureCanvasResultImage();
			}
			return edgeInspectResult;
		}

		private void PrepareSdkResultViewport()
		{
			if (ClientSize.Width < 1320 || ClientSize.Height < 780)
			{
				ClientSize = new Size(1320, 780);
			}
			InitSplitMain();
			LayoutRightPanelOnePage();
			FixRightPanelLayout();
			PerformLayout();
			splitMain.Panel1.PerformLayout();
			canvas.PerformLayout();
			canvas.FitToWindow();
			Application.DoEvents();
			canvas.Invalidate();
			canvas.Update();
		}

		private Bitmap CaptureCanvasResultImage()
		{
			int width = Math.Max(1, canvas.Width);
			int height = Math.Max(1, canvas.Height);
			Bitmap bitmap = new Bitmap(width, height);
			canvas.MinimalResultExport = true;
			try
			{
				canvas.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
			}
			finally
			{
				canvas.MinimalResultExport = false;
				canvas.Invalidate();
			}
			return bitmap;
		}

		private void FixRightWidth()
		{
			if (splitMain == null || splitMain.IsDisposed || splitMain.ClientSize.Width <= 0)
			{
				return;
			}
			int num = splitMain.ClientSize.Width;
			int num2 = Math.Max(0, splitMain.Panel1MinSize);
			int num3 = Math.Max(0, splitMain.Panel2MinSize);
			int num4 = 510;
			int val = num - num4;
			int num5 = num - num3 - splitMain.SplitterWidth;
			int num6 = num2;
			if (num5 >= num6)
			{
				int num7 = Math.Max(num6, Math.Min(val, num5));
				if (num7 >= num6 && num7 <= num5)
				{
					splitMain.SplitterDistance = num7;
				}
			}
		}

		private void FixRightPanelLayout()
		{
			try
			{
				FixMultiLineLabel(lblSummary, 10);
				FixMultiLineLabel(lblResultTip, 10);
				FixComboWidth(cboAngleRef, 170);
				FixComboWidth(cboDetectBindBase, 120);
				FixComboWidth(cboDetectMode, 120);
				FixComboWidth(cboBasePol, 100);
				FixComboWidth(cboDetPol, 100);
				splitMain.Panel2.PerformLayout();
				splitMain.Panel2.Invalidate();
			}
			catch
			{
			}
		}

		private void FixMultiLineLabel(Label lbl, int rightPadding)
		{
			if (lbl != null && lbl.Parent != null)
			{
				int num = Math.Max(60, lbl.Parent.ClientSize.Width - lbl.Left - rightPadding);
				lbl.AutoEllipsis = false;
				lbl.MaximumSize = new Size(num, 0);
				Size size = TextRenderer.MeasureText(lbl.Text ?? "", lbl.Font, new Size(num, int.MaxValue), TextFormatFlags.WordBreak);
				lbl.Size = new Size(num, Math.Max(lbl.Height, size.Height + 4));
			}
		}

		private void FixComboWidth(ComboBox cbo, int minWidth)
		{
			if (cbo == null)
			{
				return;
			}
			int num = minWidth;
			using (Graphics graphics = cbo.CreateGraphics())
			{
				num = Math.Max(num, (int)graphics.MeasureString(cbo.Text + "    ", cbo.Font).Width + 24);
				foreach (object item in cbo.Items)
				{
					if (item != null)
					{
						int num2 = (int)graphics.MeasureString(item.ToString() + "    ", cbo.Font).Width + 24;
						if (num2 > num)
						{
							num = num2;
						}
					}
				}
			}
			cbo.DropDownWidth = num;
			if (cbo.Width < num)
			{
				cbo.Width = num;
			}
		}

		private void RequestImportedDisplayRefresh()
		{
			_pendingImportedDisplayRefresh = true;
			if (base.IsHandleCreated && base.Visible)
			{
				BeginInvoke(new Action(ApplyImportedDisplayRefresh));
			}
		}

		private void ApplyImportedDisplayRefresh()
		{
			if (!_pendingImportedDisplayRefresh || base.IsDisposed || !base.Visible)
			{
				return;
			}
			FixRightWidth();
			LayoutRightPanelOnePage();
			FixRightPanelLayout();
			if (_src != null)
			{
				canvas.FitToWindow();
				_suppressRoiChanged = true;
				try
				{
					SetCanvasRoisFromJob();
					canvas.ShowRois = true;
					canvas.ShowRuntimeRois = false;
				}
				finally
				{
					_suppressRoiChanged = false;
				}
				SelectInitialCanvasTarget();
				canvas.Invalidate();
			}
			_pendingImportedDisplayRefresh = false;
		}

		private void BindEvents()
		{
			canvas.RoiChanged += delegate
			{
				if (!_suppressRoiChanged)
				{
					canvas.ShowRois = true;
					canvas.ShowRuntimeRois = false;
					canvas.ClearRuntimeRois();
					if (canvas.SelectedRoi.Kind == CanvasRoiKind.Template)
					{
						RebaseCanvasRoisToCurrentImageReference();
					}
					else
					{
						PullCanvasRoisToJob();
					}
					NormalizeDetectBaseBindings();
					RefreshBaseBindingCombo();
					LoadSelectedDetectToUi();
					LoadCurrentBaseCaliperToUi();
					LoadCurrentDetectCaliperToUi();
					ClearResultVisualsBecauseRoiChanged();
				}
			};
			canvas.SelectionChanged += delegate
			{
				UpdateSelectionHint();
				RefreshBaseBindingCombo();
				LoadSelectedDetectToUi();
				LoadCurrentBaseCaliperToUi();
				LoadCurrentDetectCaliperToUi();
			};
			chkEnableMatch.CheckedChanged += delegate
			{
				if (!_suppressUiEvents)
				{
					bool flag = chkEnableMatch.Checked;
					UpdateMatchUiState();
					_suppressRoiChanged = true;
					try
					{
						if (flag)
						{
							RebaseCanvasRoisToCurrentImageReference();
							_job.Match.Enabled = true;
							RotRectF templateRoi = ((!_lastTemplateRoi.IsEmpty) ? _lastTemplateRoi : ((!_job.TemplateRoi.IsEmpty) ? _job.TemplateRoi : ((_src == null) ? default(RotRectF) : CreateDefaultTemplateRoi())));
							_job.TemplateRoi = templateRoi;
							if (_job.TemplateSearchRoi.IsEmpty && _src != null)
							{
								_job.TemplateSearchRoi = CreateDefaultTemplateSearchRoi(templateRoi);
							}
							canvas.SetTemplateRoi(templateRoi);
							canvas.SetTemplateSearchRoi(_job.TemplateSearchRoi);
							canvas.ShowTemplateSearchRoi = _job.UseTemplateSearchRoi;
							_job.TeachData?.Clear();
							_templateModelDirty = true;
							if (!templateRoi.IsEmpty)
							{
								canvas.SetSelection(CanvasRoiKind.Template, 0);
							}
						}
						else
						{
							RebaseCanvasRoisToCurrentImageReference();
							_job.Match.Enabled = false;
							if (!canvas.TemplateRoi.IsEmpty)
							{
								_lastTemplateRoi = canvas.TemplateRoi;
							}
							else if (!_job.TemplateRoi.IsEmpty)
							{
								_lastTemplateRoi = _job.TemplateRoi;
							}
							_job.TemplateRoi = default(RotRectF);
							_job.TemplateSearchRoi = default(RotRectF);
							canvas.SetTemplateRoi(default(RotRectF));
							canvas.SetTemplateSearchRoi(default(RotRectF));
							canvas.ShowTemplateSearchRoi = false;
							if (canvas.SelectedRoi.Kind == CanvasRoiKind.Template)
							{
								SelectInitialCanvasTarget();
							}
						}
					}
					finally
					{
						_suppressRoiChanged = false;
					}
					SyncUiToJobGlobal();
					canvas.ShowRois = true;
					canvas.ShowRuntimeRois = false;
					canvas.ClearRuntimeRois();
					canvas.Invalidate();
					_resultValid = false;
					_hasUnsavedChanges = true;
					if (flag)
					{
						SetReady("READY", "已启用模板匹配：若当前无模板ROI，已自动补默认模板ROI。");
					}
					else
					{
						SetReady("READY", "已关闭模板匹配：模板ROI已隐藏。");
					}
				}
			};
			chkEnableSearchRoi.CheckedChanged += delegate
			{
				if (!_suppressUiEvents)
				{
					_job.UseTemplateSearchRoi = chkEnableSearchRoi.Checked;
					canvas.ShowTemplateSearchRoi = _job.Match.Enabled && _job.UseTemplateSearchRoi;
					SyncUiToJobGlobal();
					ClearResultVisualsBecauseRoiChanged();
					_hasUnsavedChanges = true;
					SetReady("READY", chkEnableSearchRoi.Checked ? "已启用模板搜索区域：模板匹配将限制在黄色搜索框内。" : "已关闭模板搜索区域：模板匹配将搜索整幅图像。");
				}
			};
			chkUseReferenceLine.CheckedChanged += delegate
			{
				if (!_suppressUiEvents)
				{
					SyncUiToJobGlobal();
					SyncCurrentDetectUiToJob();
					UpdateUseReferenceUiState();
					RefreshBaseBindingCombo();
					LoadSelectedDetectToUi();
					LoadCurrentBaseCaliperToUi();
					ClearResultVisualsBecauseRoiChanged();
				}
			};
			chkDetectEnabled.CheckedChanged += DetectItemUiChanged;
			cboAngleRef.SelectedIndexChanged += DetectItemUiChanged;
			cboDetectBindBase.SelectedIndexChanged += DetectItemUiChanged;
			numNominal.ValueChanged += DetectItemUiChanged;
			cboDetectMode.SelectedIndexChanged += GlobalUiParamChanged;
			numConsecutiveAbnormalPoints.ValueChanged += GlobalUiParamChanged;
			cboLanguage.SelectedIndexChanged += delegate
			{
				if (_suppressUiEvents)
				{
					return;
				}
				_job.Language = LanguageFromUi();
				ApplyLanguageToUi();
				SetReady("READY", _job.Language == InspectionLanguage.English ? "Language switched to English." : "语言已切换为中文。");
			};
			cboBasePol.SelectedIndexChanged += CurrentBaseCaliperUiChanged;
			numBaseMeasures.ValueChanged += CurrentBaseCaliperUiChanged;
			numBaseSigma.ValueChanged += CurrentBaseCaliperUiChanged;
			numBaseTh.ValueChanged += CurrentBaseCaliperUiChanged;
			numBaseOutward.ValueChanged += CurrentBaseCaliperUiChanged;
			cboDetPol.SelectedIndexChanged += CurrentDetectCaliperUiChanged;
			numDetMeasures.ValueChanged += CurrentDetectCaliperUiChanged;
			numDetSigma.ValueChanged += CurrentDetectCaliperUiChanged;
			numDetTh.ValueChanged += CurrentDetectCaliperUiChanged;
			numDetFitRatio.ValueChanged += CurrentDetectCaliperUiChanged;
			numMatchMinScore.ValueChanged += GlobalUiParamChanged;
			numMatchAngleStart.ValueChanged += GlobalUiParamChanged;
			numMatchAngleExtent.ValueChanged += GlobalUiParamChanged;
			btnLoad.Click += delegate
			{
				LoadImage();
			};
			btnAddBaseRoi.Click += delegate
			{
				if (_src == null)
				{
					MessageBox.Show("请先加载图片。");
				}
				else
				{
					AddBaseRoi();
				}
			};
			btnAddCircleBaseRoi.Click += delegate
			{
				if (_src == null)
				{
					MessageBox.Show("请先加载图片。");
				}
				else
				{
					AddCircleBaseRoi();
				}
			};
			btnAddCirclePointRoi.Click += delegate
			{
				if (_src == null)
				{
					MessageBox.Show("请先加载图片。");
				}
				else
				{
					AddCirclePointRoi();
				}
			};
			btnAddDetectRoi.Click += delegate
			{
				if (_src == null)
				{
					MessageBox.Show("请先加载图片。");
				}
				else
				{
					AddDetectRoi();
				}
			};
			btnDeleteRoi.Click += delegate
			{
				if (!canvas.SelectedRoi.IsValid)
				{
					MessageBox.Show("请先在左侧图像中选中一个基准ROI或检测ROI。");
				}
				else if (canvas.SelectedRoi.Kind == CanvasRoiKind.Template)
				{
					MessageBox.Show("模板ROI不支持删除。");
				}
				else
				{
					CanvasRoiKind kind = canvas.SelectedRoi.Kind;
					int index = canvas.SelectedRoi.Index;
					_suppressRoiChanged = true;
					try
					{
						if (!canvas.RemoveSelectedRoi())
						{
							return;
						}
					}
					finally
					{
						_suppressRoiChanged = false;
					}
					if (kind == CanvasRoiKind.Base && index >= 0 && index < _job.BaseRois.Count)
					{
						_job.BaseRois.RemoveAt(index);
					}
					else if ((kind == CanvasRoiKind.CircleBase1 || kind == CanvasRoiKind.CircleBase2) && index >= 0 && index < _job.CircleBaseRois.Count)
					{
						_job.CircleBaseRois.RemoveAt(index);
					}
					else if (kind == CanvasRoiKind.CirclePoint && index >= 0 && index < _job.CirclePointRois.Count)
					{
						_job.CirclePointRois.RemoveAt(index);
					}
					else if (kind == CanvasRoiKind.Detect && index >= 0 && index < _job.DetectItems.Count)
					{
						_job.DetectItems.RemoveAt(index);
					}
					PullCanvasRoisToJob();
					_hasUnsavedChanges = true;
					NormalizeDetectBaseBindings();
					RefreshBaseBindingCombo();
					if (_job.DetectItems.Count > 0)
					{
						SelectFirstDetectRoi();
					}
					else if (_job.BaseRois.Count > 0)
					{
						canvas.SetSelection(CanvasRoiKind.Base, 0);
					}
					else if (_job.CircleBaseRois.Count > 0)
					{
						canvas.SetSelection(CanvasRoiKind.CircleBase1, 0);
					}
					else if (_job.CirclePointRois.Count > 0)
					{
						canvas.SetSelection(CanvasRoiKind.CirclePoint, 0);
					}
					else if (!_job.TemplateRoi.IsEmpty)
					{
						canvas.SetSelection(CanvasRoiKind.Template, 0);
					}
					else
					{
						canvas.SetSelection(CanvasRoiKind.None, -1);
					}
					LoadSelectedDetectToUi();
					LoadCurrentBaseCaliperToUi();
					LoadCurrentDetectCaliperToUi();
					SetReady("READY", "已删除选中的ROI。");
				}
			};
			btnTemplateSettings.Click += delegate
			{
				OpenTemplateSettings();
			};
			btnSaveTeach.Click += delegate
			{
				if (_src == null)
				{
					MessageBox.Show("请先加载图片。");
					return;
				}
				try
				{
					_savedJob = BuildJobForReturn();
					_job = _savedJob.DeepClone();
					_hasUnsavedChanges = false;
					SetReady("已保存", _job.Match.Enabled ? "参数、各ROI独立卡尺参数与模板已保存到当前 Job。毛刺公差由上位机传入。" : "参数与各ROI独立卡尺参数已保存（模板匹配关闭）。毛刺公差由上位机传入。");
				}
				catch (Exception ex)
				{
					SetError("错误", ex.Message);
				}
			};
			btnRun.Click += delegate
			{
				if (_src == null)
				{
					MessageBox.Show("请先加载图片。");
					return;
				}
				try
				{
					EdgeInspectJob src = BuildJobForReturn();
					_job = src.DeepClone();
					EdgeInspectResult r = _proc.Inspect(_src, src.DeepClone());
					_resultValid = true;
					ShowResult(r);
				}
				catch (Exception ex)
				{
					canvas.ShowRois = false;
					canvas.ShowRuntimeRois = false;
					canvas.ClearRuntimeRois();
					_resultValid = false;
					SetError("错误", ex.Message);
				}
			};
			btnConfirm.Click += delegate
			{
				BtnConfirmInternal();
			};
			base.FormClosing += delegate(object sender, FormClosingEventArgs e)
			{
				if (!ConfirmSaveBeforeClose())
				{
					e.Cancel = true;
					return;
				}
				_src?.Dispose();
				_proc.Dispose();
			};
		}

		private void GlobalUiParamChanged(object sender, EventArgs e)
		{
			if (!_suppressUiEvents)
			{
				if (ReferenceEquals(sender, numMatchAngleStart) || ReferenceEquals(sender, numMatchAngleExtent))
				{
					_templateModelDirty = true;
				}
				SyncUiToJobGlobal();
				ClearResultVisualsBecauseRoiChanged();
			}
		}

		private void DetectItemUiChanged(object sender, EventArgs e)
		{
			if (!_suppressUiEvents)
			{
				SyncCurrentDetectUiToJob();
				LoadCurrentBaseCaliperToUi();
				ClearResultVisualsBecauseRoiChanged();
			}
		}

		private void CurrentBaseCaliperUiChanged(object sender, EventArgs e)
		{
			if (!_suppressUiEvents)
			{
				SyncCurrentBaseCaliperUiToJob();
				ClearResultVisualsBecauseRoiChanged();
			}
		}

		private void CurrentDetectCaliperUiChanged(object sender, EventArgs e)
		{
			if (!_suppressUiEvents)
			{
				SyncCurrentDetectCaliperUiToJob();
				ClearResultVisualsBecauseRoiChanged();
			}
		}

		private EdgeInspectJob BuildJobForReturn()
		{
			if (_src == null)
			{
				throw new InvalidOperationException("当前未加载图像。");
			}
			PullCanvasRoisToJob();
			NormalizeDetectBaseBindings();
			SyncUiToJobGlobal();
			SyncCurrentDetectUiToJob();
			SyncCurrentBaseCaliperUiToJob();
			SyncCurrentDetectCaliperUiToJob();
			ApplyAutoCaliperGeometry(_job);
			_job.Normalize();
			bool flag = _job.DetectItems.Any((DetectRoiItem x) => x != null && x.Enabled && !x.Roi.IsEmpty);
			bool flag2 = _job.BaseRois.Any((BaseRoiItem x) => x != null && !x.Roi.IsEmpty) || _job.CircleBaseRois.Any((CircleBaseRoiItem x) => x != null && !x.Circle1.IsEmpty && !x.Circle2.IsEmpty);
			if (!flag)
			{
				throw new InvalidOperationException("请至少设置一个启用的检测ROI。");
			}
			if (RequiresAnyReferenceBase(_job) && !flag2)
			{
				throw new InvalidOperationException("当前至少有一个检测ROI使用“基准线判定”，请至少设置一个基准ROI。");
			}
			if (_job.Match.Enabled && _job.TemplateRoi.IsEmpty)
			{
				throw new InvalidOperationException("当前已启用模板匹配，请先设置模板ROI。");
			}
			for (int num = 0; num < _job.DetectItems.Count; num++)
			{
				DetectRoiItem detectRoiItem = _job.DetectItems[num];
				if (detectRoiItem != null && detectRoiItem.Enabled && !detectRoiItem.Roi.IsEmpty)
				{
					_job.NormalizeDetectBinding(detectRoiItem);
					if (detectRoiItem.Caliper == null)
					{
						detectRoiItem.Caliper = _job.DetectCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultDetectCaliper();
					}
				}
			}
			for (int num2 = 0; num2 < _job.BaseRois.Count; num2++)
			{
				BaseRoiItem baseRoiItem = _job.BaseRois[num2];
				if (baseRoiItem != null && baseRoiItem.Caliper == null)
				{
					baseRoiItem.Caliper = _job.BaseCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultBaseCaliper();
				}
			}
			EdgeInspectJob edgeInspectJob = _job.DeepClone();
			NormalizeTemplateTransformFlags(edgeInspectJob);
			if (edgeInspectJob.Match.Enabled)
			{
				if (_templateModelDirty || !HasValidTeachData(edgeInspectJob))
				{
					List<PointF> featurePoints = edgeInspectJob.TeachData?.FeaturePoints;
					int realPointCount = featurePoints == null ? 0 : featurePoints.Count((PointF x) => !float.IsNaN(x.X) && !float.IsNaN(x.Y));
					if (realPointCount >= 12)
					{
						edgeInspectJob.TeachData = _proc.TeachFromOuterContourPoints(_src, edgeInspectJob, featurePoints);
					}
					else
					{
						if (!_editTransform.IsIdentity)
						{
							throw new InvalidOperationException("模板参考图与当前图像姿态不同，且没有足够的参考轮廓点用于重建模型。请先打开模板设置，将当前图像设为新的模板参考图。");
						}
						edgeInspectJob.TeachData = _proc.Teach(_src, edgeInspectJob);
					}
				}
				_templateModelDirty = false;
			}
			else
			{
				edgeInspectJob.TeachData.Clear();
			}
			return edgeInspectJob;
		}

		private void SyncUiToJobGlobal()
		{
			if (GetCurrentDetectIndex() < 0)
			{
				_job.UseReferenceLine = chkUseReferenceLine.Checked;
			}
			_job.DetectMode = DetectModeFromUi();
			_job.ConsecutiveAbnormalPointCount = (int)numConsecutiveAbnormalPoints.Value;
			_job.Match.Enabled = chkEnableMatch.Checked;
			_job.UseTemplateSearchRoi = chkEnableSearchRoi.Checked;
			_job.Language = LanguageFromUi();
			_job.Match.MinScore = (double)numMatchMinScore.Value;
			_job.Match.AngleStart = (double)numMatchAngleStart.Value;
			_job.Match.AngleExtent = (double)numMatchAngleExtent.Value;
			PullCanvasRoisToJob();
			NormalizeDetectBaseBindings();
			_job.Normalize();
		}

		private void OpenTemplateSettings()
		{
			if (_src == null)
			{
				MessageBox.Show("请先加载图片。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				return;
			}
			if (!_editTransformResolved)
			{
				MessageBox.Show("当前图像未找到模板，无法确定 ROI 的当前坐标。请加载能正常匹配的图像后再修改模板。", "模板未匹配", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				return;
			}
			PullCanvasRoisToJob();
			SyncUiToJobGlobal();
			EdgeInspectJob currentImageJob = CreateCurrentImageReferenceJob();
			using (TemplateSettingsForm templateSettingsForm = new TemplateSettingsForm(_src, currentImageJob))
			{
				if (templateSettingsForm.ShowDialog(this) != DialogResult.OK)
				{
					return;
				}
				EdgeInspectJob job = templateSettingsForm.Job;
				if (job == null)
				{
					return;
				}
				_job = job.DeepClone();
				NormalizeTemplateTransformFlags(_job);
				_editTransform = RigidEditTransform.Identity;
				_editTransformResolved = true;
				_templateModelDirty = !HasValidTeachData(_job);
				canvas.AllowRoiEditing = true;
				_hasUnsavedChanges = true;
				_suppressUiEvents = true;
				_suppressRoiChanged = true;
				try
				{
					LoadJobToUi(_job);
					SetCanvasRoisFromJob();
					canvas.ShowRois = true;
					canvas.ShowRuntimeRois = false;
					canvas.ClearRuntimeRois();
				}
				finally
				{
					_suppressUiEvents = false;
					_suppressRoiChanged = false;
				}
				SelectInitialCanvasTarget();
				RefreshBaseBindingCombo();
				LoadSelectedDetectToUi();
				LoadCurrentBaseCaliperToUi();
				LoadCurrentDetectCaliperToUi();
				ClearResultVisualsBecauseRoiChanged();
				SetReady("READY", "模板设置已更新。");
			}
		}

		private void SyncCurrentDetectUiToJob()
		{
			int currentDetectIndex = GetCurrentDetectIndex();
			if (currentDetectIndex < 0 || currentDetectIndex >= _job.DetectItems.Count)
			{
				return;
			}
			DetectRoiItem detectRoiItem = _job.DetectItems[currentDetectIndex];
			if (detectRoiItem == null)
			{
				return;
			}
			detectRoiItem.Enabled = chkDetectEnabled.Checked;
			detectRoiItem.UseReferenceLine = chkUseReferenceLine.Checked;
			detectRoiItem.NominalDistancePx = MmToPx((double)numNominal.Value);
			detectRoiItem.AngleReference = AngleRefFromUi();
			BaseBindingOption selectedBaseBindingOption = GetSelectedBaseBindingOption();
			if (selectedBaseBindingOption == null)
			{
				detectRoiItem.BaseRoiIndex = 0;
				detectRoiItem.BaseRoiId = "";
				detectRoiItem.CircleBaseRoiIndex = 0;
				detectRoiItem.CircleBaseRoiId = "";
				detectRoiItem.CirclePointRoiIndex = 0;
				detectRoiItem.CirclePointRoiId = "";
			}
			else
			{
				detectRoiItem.ReferenceBaseKind = selectedBaseBindingOption.Kind;
				if (selectedBaseBindingOption.Kind == ReferenceBaseKind.CirclePair)
				{
					detectRoiItem.UseReferenceLine = true;
					detectRoiItem.CircleBaseRoiIndex = selectedBaseBindingOption.Index;
					detectRoiItem.CircleBaseRoiId = selectedBaseBindingOption.Id ?? "";
				}
				else if (selectedBaseBindingOption.Kind == ReferenceBaseKind.CirclePoint)
				{
					detectRoiItem.UseReferenceLine = false;
					detectRoiItem.CirclePointRoiIndex = selectedBaseBindingOption.Index;
					detectRoiItem.CirclePointRoiId = selectedBaseBindingOption.Id ?? "";
				}
				else
				{
					detectRoiItem.UseReferenceLine = true;
					detectRoiItem.BaseRoiIndex = selectedBaseBindingOption.Index;
					detectRoiItem.BaseRoiId = selectedBaseBindingOption.Id ?? "";
				}
			}
			if (detectRoiItem.Caliper == null)
			{
				detectRoiItem.Caliper = _job.DetectCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultDetectCaliper();
			}
			_job.Normalize();
		}

		private void SyncCurrentBaseCaliperUiToJob()
		{
			int circlePointCaliperEditorTargetIndex = GetCirclePointCaliperEditorTargetIndex();
			if (circlePointCaliperEditorTargetIndex >= 0 && circlePointCaliperEditorTargetIndex < _job.CirclePointRois.Count)
			{
				CirclePointRoiItem circlePointRoiItem = _job.CirclePointRois[circlePointCaliperEditorTargetIndex];
				if (circlePointRoiItem != null)
				{
					if (circlePointRoiItem.Caliper == null)
					{
						circlePointRoiItem.Caliper = _job.CircleCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultCircleCaliper();
					}
					string transition = PolToTransition(cboBasePol.SelectedIndex);
					if (transition != null)
					{
						circlePointRoiItem.Caliper.Transition = transition;
					}
					circlePointRoiItem.Caliper.MeasureSelect = "first";
					circlePointRoiItem.Caliper.NumMeasures = (int)numBaseMeasures.Value;
					circlePointRoiItem.Caliper.Sigma = (double)numBaseSigma.Value;
					circlePointRoiItem.Caliper.Threshold = (double)numBaseTh.Value;
					circlePointRoiItem.Caliper.SearchOutward = (double)numBaseOutward.Value;
					_job.CircleCaliper = circlePointRoiItem.Caliper.DeepClone();
					_job.Normalize();
				}
				return;
			}
			int circleBaseCaliperEditorTargetIndex = GetCircleBaseCaliperEditorTargetIndex();
			if (circleBaseCaliperEditorTargetIndex >= 0 && circleBaseCaliperEditorTargetIndex < _job.CircleBaseRois.Count)
			{
				CircleBaseRoiItem circleBaseRoiItem = _job.CircleBaseRois[circleBaseCaliperEditorTargetIndex];
				if (circleBaseRoiItem != null)
				{
					if (circleBaseRoiItem.Caliper == null)
					{
						circleBaseRoiItem.Caliper = _job.CircleCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultCircleCaliper();
					}
					string transition2 = PolToTransition(cboBasePol.SelectedIndex);
					if (transition2 != null)
					{
						circleBaseRoiItem.Caliper.Transition = transition2;
					}
					circleBaseRoiItem.Caliper.MeasureSelect = "first";
					circleBaseRoiItem.Caliper.NumMeasures = (int)numBaseMeasures.Value;
					circleBaseRoiItem.Caliper.Sigma = (double)numBaseSigma.Value;
					circleBaseRoiItem.Caliper.Threshold = (double)numBaseTh.Value;
					circleBaseRoiItem.Caliper.SearchOutward = (double)numBaseOutward.Value;
					_job.CircleCaliper = circleBaseRoiItem.Caliper.DeepClone();
					_job.Normalize();
				}
				return;
			}
			int baseCaliperEditorTargetIndex = GetBaseCaliperEditorTargetIndex();
			if (baseCaliperEditorTargetIndex < 0 || baseCaliperEditorTargetIndex >= _job.BaseRois.Count)
			{
				return;
			}
			BaseRoiItem baseRoiItem = _job.BaseRois[baseCaliperEditorTargetIndex];
			if (baseRoiItem != null)
			{
				if (baseRoiItem.Caliper == null)
				{
					baseRoiItem.Caliper = _job.BaseCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultBaseCaliper();
				}
				string transition3 = PolToTransition(cboBasePol.SelectedIndex);
				if (transition3 != null)
				{
					baseRoiItem.Caliper.Transition = transition3;
				}
				baseRoiItem.Caliper.MeasureSelect = "first";
				baseRoiItem.Caliper.NumMeasures = (int)numBaseMeasures.Value;
				baseRoiItem.Caliper.Sigma = (double)numBaseSigma.Value;
				baseRoiItem.Caliper.Threshold = (double)numBaseTh.Value;
				_job.BaseCaliper = baseRoiItem.Caliper.DeepClone();
				_job.Normalize();
			}
		}

		private void SyncCurrentDetectCaliperUiToJob()
		{
			int detectCaliperEditorTargetIndex = GetDetectCaliperEditorTargetIndex();
			if (detectCaliperEditorTargetIndex < 0 || detectCaliperEditorTargetIndex >= _job.DetectItems.Count)
			{
				return;
			}
			DetectRoiItem detectRoiItem = _job.DetectItems[detectCaliperEditorTargetIndex];
			if (detectRoiItem != null)
			{
				if (detectRoiItem.Caliper == null)
				{
					detectRoiItem.Caliper = _job.DetectCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultDetectCaliper();
				}
				string transition = PolToTransition(cboDetPol.SelectedIndex);
				if (transition != null)
				{
					detectRoiItem.Caliper.Transition = transition;
				}
				detectRoiItem.Caliper.MeasureSelect = "first";
				detectRoiItem.Caliper.NumMeasures = (int)numDetMeasures.Value;
				detectRoiItem.Caliper.Sigma = (double)numDetSigma.Value;
				detectRoiItem.Caliper.Threshold = (double)numDetTh.Value;
				detectRoiItem.Caliper.LineFitNormalRatio = (double)numDetFitRatio.Value / 100.0;
				_job.DetectCaliper = detectRoiItem.Caliper.DeepClone();
				_job.Normalize();
			}
		}

		private void PullCanvasRoisToJob()
		{
			_job.TemplateRoi = _editTransform.ToReference(canvas.TemplateRoi);
			_job.TemplateSearchRoi = canvas.TemplateSearchRoi;
			while (_job.BaseRois.Count < canvas.BaseRois.Count)
			{
				_job.BaseRois.Add(new BaseRoiItem
				{
					Name = $"基准{_job.BaseRois.Count + 1}",
					UseTemplateTransform = true,
					Caliper = (_job.BaseCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultBaseCaliper())
				});
			}
			while (_job.BaseRois.Count > canvas.BaseRois.Count)
			{
				_job.BaseRois.RemoveAt(_job.BaseRois.Count - 1);
			}
			for (int i = 0; i < canvas.BaseRois.Count; i++)
			{
				_job.BaseRois[i].Roi = _editTransform.ToReference(canvas.BaseRois[i]);
				_job.BaseRois[i].UseTemplateTransform = true;
				if (string.IsNullOrWhiteSpace(_job.BaseRois[i].Name))
				{
					_job.BaseRois[i].Name = $"基准{i + 1}";
				}
				if (_job.BaseRois[i].Caliper == null)
				{
					_job.BaseRois[i].Caliper = _job.BaseCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultBaseCaliper();
				}
			}
			while (_job.CircleBaseRois.Count < canvas.CircleBaseRois.Count)
			{
				_job.CircleBaseRois.Add(new CircleBaseRoiItem
				{
					Name = $"圆基准{_job.CircleBaseRois.Count + 1}",
					UseTemplateTransform = true,
					Caliper = (_job.CircleCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultCircleCaliper())
				});
			}
			while (_job.CircleBaseRois.Count > canvas.CircleBaseRois.Count)
			{
				_job.CircleBaseRois.RemoveAt(_job.CircleBaseRois.Count - 1);
			}
			for (int j = 0; j < canvas.CircleBaseRois.Count; j++)
			{
				_job.CircleBaseRois[j].Circle1 = _editTransform.ToReference(canvas.CircleBaseRois[j].Circle1);
				_job.CircleBaseRois[j].Circle2 = _editTransform.ToReference(canvas.CircleBaseRois[j].Circle2);
				_job.CircleBaseRois[j].UseTemplateTransform = true;
				if (string.IsNullOrWhiteSpace(_job.CircleBaseRois[j].Name))
				{
					_job.CircleBaseRois[j].Name = $"圆基准{j + 1}";
				}
				if (_job.CircleBaseRois[j].Caliper == null)
				{
					_job.CircleBaseRois[j].Caliper = _job.CircleCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultCircleCaliper();
				}
			}
			while (_job.CirclePointRois.Count < canvas.CirclePointRois.Count)
			{
				_job.CirclePointRois.Add(new CirclePointRoiItem
				{
					Name = $"圆点基准{_job.CirclePointRois.Count + 1}",
					UseTemplateTransform = true,
					Caliper = (_job.CircleCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultCircleCaliper())
				});
			}
			while (_job.CirclePointRois.Count > canvas.CirclePointRois.Count)
			{
				_job.CirclePointRois.RemoveAt(_job.CirclePointRois.Count - 1);
			}
			for (int k = 0; k < canvas.CirclePointRois.Count; k++)
			{
				_job.CirclePointRois[k].Circle = _editTransform.ToReference(canvas.CirclePointRois[k]);
				_job.CirclePointRois[k].UseTemplateTransform = true;
				if (string.IsNullOrWhiteSpace(_job.CirclePointRois[k].Name))
				{
					_job.CirclePointRois[k].Name = $"圆点基准{k + 1}";
				}
				if (_job.CirclePointRois[k].Caliper == null)
				{
					_job.CirclePointRois[k].Caliper = _job.CircleCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultCircleCaliper();
				}
			}
			while (_job.DetectItems.Count < canvas.DetectRois.Count)
			{
				_job.DetectItems.Add(new DetectRoiItem
				{
					Name = $"检测{_job.DetectItems.Count + 1}",
					UseTemplateTransform = true,
					BaseRoiIndex = 0,
					BaseRoiId = ((_job.BaseRois.Count > 0) ? (_job.BaseRois[0]?.Id ?? "") : ""),
					Caliper = (_job.DetectCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultDetectCaliper())
				});
			}
			while (_job.DetectItems.Count > canvas.DetectRois.Count)
			{
				_job.DetectItems.RemoveAt(_job.DetectItems.Count - 1);
			}
			for (int l = 0; l < canvas.DetectRois.Count; l++)
			{
				_job.DetectItems[l].Roi = _editTransform.ToReference(canvas.DetectRois[l]);
				_job.DetectItems[l].UseTemplateTransform = true;
				if (string.IsNullOrWhiteSpace(_job.DetectItems[l].Name))
				{
					_job.DetectItems[l].Name = $"检测{l + 1}";
				}
				if (_job.DetectItems[l].Caliper == null)
				{
					_job.DetectItems[l].Caliper = _job.DetectCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultDetectCaliper();
				}
				_job.NormalizeDetectBinding(_job.DetectItems[l]);
			}
			_job.Normalize();
		}

		private void NormalizeDetectBaseBindings()
		{
			for (int i = 0; i < _job.DetectItems.Count; i++)
			{
				DetectRoiItem detectRoiItem = _job.DetectItems[i];
				if (detectRoiItem != null)
				{
					_job.NormalizeDetectBinding(detectRoiItem);
				}
			}
		}

		private int ResolveDetectBoundBaseIndex(DetectRoiItem det)
		{
			if (det == null)
			{
				return (_job.BaseRois.Count <= 0) ? (-1) : 0;
			}
			if (_job.BaseRois.Count <= 0)
			{
				return -1;
			}
			return _job.ResolveBaseRoiIndex(det.BaseRoiId, det.BaseRoiIndex);
		}

		private int ResolveDetectBoundCircleBaseIndex(DetectRoiItem det)
		{
			if (det == null)
			{
				return (_job.CircleBaseRois.Count <= 0) ? (-1) : 0;
			}
			if (_job.CircleBaseRois.Count <= 0)
			{
				return -1;
			}
			return _job.ResolveCircleBaseRoiIndex(det.CircleBaseRoiId, det.CircleBaseRoiIndex);
		}

		private BaseBindingOption GetSelectedBaseBindingOption()
		{
			int selectedIndex = cboDetectBindBase.SelectedIndex;
			if (selectedIndex < 0 || selectedIndex >= _baseBindingOptions.Count)
			{
				return null;
			}
			return _baseBindingOptions[selectedIndex];
		}

		private int FindBindingOptionIndex(DetectRoiItem det)
		{
			if (det == null)
			{
				return (_baseBindingOptions.Count <= 0) ? (-1) : 0;
			}
			for (int i = 0; i < _baseBindingOptions.Count; i++)
			{
				BaseBindingOption baseBindingOption = _baseBindingOptions[i];
				if (baseBindingOption.Kind == det.ReferenceBaseKind)
				{
					string text = det.BaseRoiId;
					int num = det.BaseRoiIndex;
					if (baseBindingOption.Kind == ReferenceBaseKind.CirclePair)
					{
						text = det.CircleBaseRoiId;
						num = det.CircleBaseRoiIndex;
					}
					else if (baseBindingOption.Kind == ReferenceBaseKind.CirclePoint)
					{
						text = det.CirclePointRoiId;
						num = det.CirclePointRoiIndex;
					}
					if (!string.IsNullOrWhiteSpace(text) && string.Equals(baseBindingOption.Id, text, StringComparison.OrdinalIgnoreCase))
					{
						return i;
					}
					if (string.IsNullOrWhiteSpace(text) && baseBindingOption.Index == num)
					{
						return i;
					}
				}
			}
			return (_baseBindingOptions.Count <= 0) ? (-1) : 0;
		}

		private static bool HasValidTeachData(EdgeInspectJob job)
		{
			return job?.TeachData != null && job.TeachData.HasTemplate && job.TeachData.ModelBytes != null && job.TeachData.ModelBytes.Length > 0;
		}

		private static void NormalizeTemplateTransformFlags(EdgeInspectJob job)
		{
			if (job == null)
			{
				return;
			}
			foreach (BaseRoiItem item in job.BaseRois.Where((BaseRoiItem x) => x != null))
			{
				item.UseTemplateTransform = true;
			}
			foreach (CircleBaseRoiItem item2 in job.CircleBaseRois.Where((CircleBaseRoiItem x) => x != null))
			{
				item2.UseTemplateTransform = true;
			}
			foreach (CirclePointRoiItem item3 in job.CirclePointRois.Where((CirclePointRoiItem x) => x != null))
			{
				item3.UseTemplateTransform = true;
			}
			foreach (DetectRoiItem item4 in job.DetectItems.Where((DetectRoiItem x) => x != null))
			{
				item4.UseTemplateTransform = true;
			}
		}

		private void ResolveEditTransformForCurrentImage()
		{
			_editTransform = RigidEditTransform.Identity;
			_editTransformResolved = true;
			if (_src != null && _job.Match.Enabled && HasValidTeachData(_job))
			{
				try
				{
					TemplateMatchQuickTestResult result = _proc.TestTemplateMatch(_src, _job.DeepClone());
					if (result != null && result.Found)
					{
						_editTransform = new RigidEditTransform
						{
							RefX = _job.TeachData.RefCol,
							RefY = _job.TeachData.RefRow,
							ImageX = result.Col,
							ImageY = result.Row,
							Angle = RotRectF.NormalizeAngle((float)(_job.TeachData.RefAngle - result.Angle))
						};
					}
					else
					{
						_editTransformResolved = false;
					}
				}
				catch
				{
					_editTransformResolved = false;
				}
			}
			canvas.AllowRoiEditing = _editTransformResolved;
		}

		private void SetCanvasRoisFromJob()
		{
			canvas.SetAllRois(_editTransform.ToImage(_job.TemplateRoi), _job.BaseRois.Select((BaseRoiItem x) => _editTransform.ToImage(x.Roi)), _job.CircleBaseRois.Select((CircleBaseRoiItem x) => new CircleBaseRoiPair
			{
				Circle1 = _editTransform.ToImage(x.Circle1),
				Circle2 = _editTransform.ToImage(x.Circle2)
			}), _job.CirclePointRois.Select((CirclePointRoiItem x) => _editTransform.ToImage(x.Circle)), _job.DetectItems.Select((DetectRoiItem x) => _editTransform.ToImage(x.Roi)));
			canvas.SetTemplateSearchRoi(_job.TemplateSearchRoi);
			canvas.AllowRoiEditing = _editTransformResolved;
			canvas.ShowTemplateSearchRoi = _job.Match.Enabled && _job.UseTemplateSearchRoi;
		}

		private void RebaseCanvasRoisToCurrentImageReference()
		{
			if (!_editTransformResolved)
			{
				return;
			}
			_editTransform = RigidEditTransform.Identity;
			PullCanvasRoisToJob();
			_job.TeachData?.Clear();
			NormalizeTemplateTransformFlags(_job);
			_templateModelDirty = true;
			canvas.AllowRoiEditing = true;
		}

		private EdgeInspectJob CreateCurrentImageReferenceJob()
		{
			EdgeInspectJob result = _job.DeepClone();
			NormalizeTemplateTransformFlags(result);
			if (_editTransform.IsIdentity)
			{
				return result;
			}
			result.TemplateRoi = _editTransform.ToImage(result.TemplateRoi);
			foreach (BaseRoiItem item in result.BaseRois.Where((BaseRoiItem x) => x != null))
			{
				item.Roi = _editTransform.ToImage(item.Roi);
			}
			foreach (CircleBaseRoiItem item2 in result.CircleBaseRois.Where((CircleBaseRoiItem x) => x != null))
			{
				item2.Circle1 = _editTransform.ToImage(item2.Circle1);
				item2.Circle2 = _editTransform.ToImage(item2.Circle2);
			}
			foreach (CirclePointRoiItem item3 in result.CirclePointRois.Where((CirclePointRoiItem x) => x != null))
			{
				item3.Circle = _editTransform.ToImage(item3.Circle);
			}
			foreach (DetectRoiItem item4 in result.DetectItems.Where((DetectRoiItem x) => x != null))
			{
				item4.Roi = _editTransform.ToImage(item4.Roi);
			}
			List<PointF> points = result.TeachData?.FeaturePoints == null ? new List<PointF>() : result.TeachData.FeaturePoints.Select(_editTransform.ToImage).ToList();
			List<TemplateEraseStroke> strokes = result.TeachData?.EraseStrokes == null ? new List<TemplateEraseStroke>() : result.TeachData.EraseStrokes.Where((TemplateEraseStroke x) => x != null).Select((TemplateEraseStroke x) => new TemplateEraseStroke
			{
				Center = _editTransform.ToImage(x.Center),
				Radius = x.Radius
			}).ToList();
			if (result.TeachData == null)
			{
				result.TeachData = new TemplateTeachData();
			}
			result.TeachData.Clear();
			result.TeachData.FeaturePoints = points;
			result.TeachData.EraseStrokes = strokes;
			result.TeachData.RefCol = result.TemplateRoi.Center.X;
			result.TeachData.RefRow = result.TemplateRoi.Center.Y;
			return result;
		}

		private static CircleBaseRoiPair ToCanvasCirclePair(CircleBaseRoiItem item)
		{
			return new CircleBaseRoiPair
			{
				Circle1 = (item?.Circle1 ?? default(CircleRoiF)),
				Circle2 = (item?.Circle2 ?? default(CircleRoiF))
			};
		}

		private void LoadJobToUi(EdgeInspectJob job)
		{
			if (job != null)
			{
				chkEnableMatch.Checked = job.Match.Enabled;
				chkEnableSearchRoi.Checked = job.UseTemplateSearchRoi;
				SetLanguageComboSelection(job.Language);
				chkUseReferenceLine.Checked = job.UseReferenceLine;
				cboDetectMode.SelectedIndex = DetectModeToIndex(job.DetectMode);
				numConsecutiveAbnormalPoints.Value = ClampDecimal(job.ConsecutiveAbnormalPointCount, numConsecutiveAbnormalPoints);
				numMatchMinScore.Value = ClampDecimal((decimal)job.Match.MinScore, numMatchMinScore);
				numMatchAngleStart.Value = ClampDecimal((decimal)job.Match.AngleStart, numMatchAngleStart);
				numMatchAngleExtent.Value = ClampDecimal((decimal)job.Match.AngleExtent, numMatchAngleExtent);
				UpdateMatchUiState();
				UpdateUseReferenceUiState();
				ApplyLanguageToUi();
				FixRightPanelLayout();
			}
		}

		private void LoadSelectedDetectToUi()
		{
			int currentDetectIndex = GetCurrentDetectIndex();
			bool flag = currentDetectIndex >= 0 && currentDetectIndex < _job.DetectItems.Count;
			SetDetectEditorEnabled(flag);
			RefreshBaseBindingCombo();
			if (!flag)
			{
				int currentBaseIndex = GetCurrentBaseIndex();
				int currentCircleBaseIndex = GetCurrentCircleBaseIndex();
				if (currentBaseIndex >= 0 && currentBaseIndex < _job.BaseRois.Count)
				{
					grpParams.Text = "参数（当前：" + _job.BaseRois[currentBaseIndex].Name + "）";
				}
				else if (currentCircleBaseIndex >= 0 && currentCircleBaseIndex < _job.CircleBaseRois.Count)
				{
					grpParams.Text = "参数（当前：" + _job.CircleBaseRois[currentCircleBaseIndex].Name + "）";
				}
				else if (GetCurrentCirclePointIndex() >= 0)
				{
					grpParams.Text = "参数（当前：" + _job.CirclePointRois[GetCurrentCirclePointIndex()].Name + "）";
				}
				else
				{
					grpParams.Text = "参数（当前未选中检测ROI）";
				}
				bool previousSuppressUiEvents = _suppressUiEvents;
				_suppressUiEvents = true;
				try
				{
					chkDetectEnabled.Checked = false;
					chkUseReferenceLine.Checked = _job.UseReferenceLine;
					numNominal.Value = ClampDecimal(0m, numNominal);
					cboAngleRef.SelectedIndex = 0;
					cboDetectBindBase.SelectedIndex = ((cboDetectBindBase.Items.Count <= 0) ? (-1) : 0);
				}
				finally
				{
					_suppressUiEvents = previousSuppressUiEvents;
				}
				UpdateUseReferenceUiState();
				FixRightPanelLayout();
				return;
			}
			DetectRoiItem detectRoiItem = _job.DetectItems[currentDetectIndex];
			grpParams.Text = "参数（当前：" + detectRoiItem.Name + "）";
			bool previousSuppressUiEvents2 = _suppressUiEvents;
			_suppressUiEvents = true;
			try
			{
				chkDetectEnabled.Checked = detectRoiItem.Enabled;
				chkUseReferenceLine.Checked = detectRoiItem.UseReferenceLine;
				numNominal.Value = ClampDecimal((decimal)PxToMm(detectRoiItem.NominalDistancePx), numNominal);
				cboAngleRef.SelectedIndex = ((detectRoiItem.AngleReference == DetectAngleReferenceMode.Horizontal) ? 1 : ((detectRoiItem.AngleReference == DetectAngleReferenceMode.Vertical) ? 2 : 0));
				if (cboDetectBindBase.Items.Count > 0)
				{
					int num = FindBindingOptionIndex(detectRoiItem);
					if (num < 0)
					{
						num = 0;
					}
					if (num >= cboDetectBindBase.Items.Count)
					{
						num = cboDetectBindBase.Items.Count - 1;
					}
					cboDetectBindBase.SelectedIndex = num;
				}
				else
				{
					cboDetectBindBase.SelectedIndex = -1;
				}
			}
			finally
			{
				_suppressUiEvents = previousSuppressUiEvents2;
			}
			UpdateUseReferenceUiState();
			FixRightPanelLayout();
		}

		private void LoadCurrentBaseCaliperToUi()
		{
			int circlePointCaliperEditorTargetIndex = GetCirclePointCaliperEditorTargetIndex();
			if (circlePointCaliperEditorTargetIndex >= 0 && circlePointCaliperEditorTargetIndex < _job.CirclePointRois.Count)
			{
				SetBaseCaliperEditorEnabled(enabled: true, circleOutwardEnabled: true);
				bool previousSuppressUiEvents = _suppressUiEvents;
				_suppressUiEvents = true;
				try
				{
					CaliperParameters caliperParameters = _job.CirclePointRois[circlePointCaliperEditorTargetIndex]?.Caliper ?? _job.CircleCaliper ?? EdgeInspectJob.CreateDefaultCircleCaliper();
					cboBasePol.SelectedIndex = TransitionToPolIndex(caliperParameters.Transition);
					numBaseMeasures.Value = ClampDecimal(caliperParameters.NumMeasures, numBaseMeasures);
					numBaseSigma.Value = ClampDecimal((decimal)caliperParameters.Sigma, numBaseSigma);
					numBaseTh.Value = ClampDecimal((decimal)caliperParameters.Threshold, numBaseTh);
					numBaseOutward.Value = ClampDecimal((decimal)caliperParameters.SearchOutward, numBaseOutward);
					return;
				}
				finally
				{
					_suppressUiEvents = previousSuppressUiEvents;
				}
			}
			int circleBaseCaliperEditorTargetIndex = GetCircleBaseCaliperEditorTargetIndex();
			if (circleBaseCaliperEditorTargetIndex >= 0 && circleBaseCaliperEditorTargetIndex < _job.CircleBaseRois.Count)
			{
				SetBaseCaliperEditorEnabled(enabled: true, circleOutwardEnabled: true);
				bool previousSuppressUiEvents2 = _suppressUiEvents;
				_suppressUiEvents = true;
				try
				{
					CaliperParameters caliperParameters2 = _job.CircleBaseRois[circleBaseCaliperEditorTargetIndex]?.Caliper ?? _job.CircleCaliper ?? EdgeInspectJob.CreateDefaultCircleCaliper();
					cboBasePol.SelectedIndex = TransitionToPolIndex(caliperParameters2.Transition);
					numBaseMeasures.Value = ClampDecimal(caliperParameters2.NumMeasures, numBaseMeasures);
					numBaseSigma.Value = ClampDecimal((decimal)caliperParameters2.Sigma, numBaseSigma);
					numBaseTh.Value = ClampDecimal((decimal)caliperParameters2.Threshold, numBaseTh);
					numBaseOutward.Value = ClampDecimal((decimal)caliperParameters2.SearchOutward, numBaseOutward);
					return;
				}
				finally
				{
					_suppressUiEvents = previousSuppressUiEvents2;
				}
			}
			int baseCaliperEditorTargetIndex = GetBaseCaliperEditorTargetIndex();
			bool flag = baseCaliperEditorTargetIndex >= 0 && baseCaliperEditorTargetIndex < _job.BaseRois.Count;
			SetBaseCaliperEditorEnabled(flag);
			bool previousSuppressUiEvents3 = _suppressUiEvents;
			_suppressUiEvents = true;
			try
			{
				if (!flag)
				{
					cboBasePol.SelectedIndex = 0;
					numBaseMeasures.Value = ClampDecimal(30m, numBaseMeasures);
					numBaseSigma.Value = ClampDecimal(1.0m, numBaseSigma);
					numBaseTh.Value = ClampDecimal(20m, numBaseTh);
					numBaseOutward.Value = ClampDecimal(0m, numBaseOutward);
				}
				else
				{
					CaliperParameters caliperParameters3 = _job.BaseRois[baseCaliperEditorTargetIndex]?.Caliper ?? _job.BaseCaliper ?? EdgeInspectJob.CreateDefaultBaseCaliper();
					cboBasePol.SelectedIndex = TransitionToPolIndex(caliperParameters3.Transition);
					numBaseMeasures.Value = ClampDecimal(caliperParameters3.NumMeasures, numBaseMeasures);
					numBaseSigma.Value = ClampDecimal((decimal)caliperParameters3.Sigma, numBaseSigma);
					numBaseTh.Value = ClampDecimal((decimal)caliperParameters3.Threshold, numBaseTh);
					numBaseOutward.Value = ClampDecimal(0m, numBaseOutward);
				}
			}
			finally
			{
				_suppressUiEvents = previousSuppressUiEvents3;
			}
		}

		private void LoadCurrentDetectCaliperToUi()
		{
			int detectCaliperEditorTargetIndex = GetDetectCaliperEditorTargetIndex();
			bool flag = detectCaliperEditorTargetIndex >= 0 && detectCaliperEditorTargetIndex < _job.DetectItems.Count;
			SetDetectCaliperEditorEnabled(flag);
			bool previousSuppressUiEvents = _suppressUiEvents;
			_suppressUiEvents = true;
			try
			{
				if (!flag)
				{
					cboDetPol.SelectedIndex = 0;
					numDetMeasures.Value = ClampDecimal(40m, numDetMeasures);
					numDetSigma.Value = ClampDecimal(1.0m, numDetSigma);
					numDetTh.Value = ClampDecimal(15m, numDetTh);
					numDetFitRatio.Value = ClampDecimal(60m, numDetFitRatio);
				}
				else
				{
					CaliperParameters caliperParameters = _job.DetectItems[detectCaliperEditorTargetIndex]?.Caliper ?? _job.DetectCaliper ?? EdgeInspectJob.CreateDefaultDetectCaliper();
					cboDetPol.SelectedIndex = TransitionToPolIndex(caliperParameters.Transition);
					numDetMeasures.Value = ClampDecimal(caliperParameters.NumMeasures, numDetMeasures);
					numDetSigma.Value = ClampDecimal((decimal)caliperParameters.Sigma, numDetSigma);
					numDetTh.Value = ClampDecimal((decimal)caliperParameters.Threshold, numDetTh);
					numDetFitRatio.Value = ClampDecimal((decimal)(caliperParameters.LineFitNormalRatio * 100.0), numDetFitRatio);
				}
			}
			finally
			{
				_suppressUiEvents = previousSuppressUiEvents;
			}
		}

		private void RefreshBaseBindingCombo()
		{
			int selectedIndex = cboDetectBindBase.SelectedIndex;
			bool previousSuppressUiEvents = _suppressUiEvents;
			_suppressUiEvents = true;
			try
			{
				cboDetectBindBase.Items.Clear();
				_baseBindingOptions.Clear();
				for (int i = 0; i < _job.CirclePointRois.Count; i++)
				{
					string text = (string.IsNullOrWhiteSpace(_job.CirclePointRois[i]?.Name) ? $"圆点基准{i + 1}" : _job.CirclePointRois[i].Name);
					_baseBindingOptions.Add(new BaseBindingOption
					{
						Kind = ReferenceBaseKind.CirclePoint,
						Index = i,
						Id = (_job.CirclePointRois[i]?.Id ?? ""),
						Name = text
					});
					cboDetectBindBase.Items.Add("[圆点] " + text);
				}
				for (int j = 0; j < _job.BaseRois.Count; j++)
				{
					if (!chkUseReferenceLine.Checked)
					{
						break;
					}
					string text2 = (string.IsNullOrWhiteSpace(_job.BaseRois[j]?.Name) ? $"基准{j + 1}" : _job.BaseRois[j].Name);
					_baseBindingOptions.Add(new BaseBindingOption
					{
						Kind = ReferenceBaseKind.LineRoi,
						Index = j,
						Id = (_job.BaseRois[j]?.Id ?? ""),
						Name = text2
					});
					cboDetectBindBase.Items.Add("[线] " + text2);
				}
				for (int k = 0; k < _job.CircleBaseRois.Count; k++)
				{
					if (!chkUseReferenceLine.Checked)
					{
						break;
					}
					string text3 = (string.IsNullOrWhiteSpace(_job.CircleBaseRois[k]?.Name) ? $"圆基准{k + 1}" : _job.CircleBaseRois[k].Name);
					_baseBindingOptions.Add(new BaseBindingOption
					{
						Kind = ReferenceBaseKind.CirclePair,
						Index = k,
						Id = (_job.CircleBaseRois[k]?.Id ?? ""),
						Name = text3
					});
					cboDetectBindBase.Items.Add("[圆] " + text3);
				}
				if (cboDetectBindBase.Items.Count == 0)
				{
					cboDetectBindBase.SelectedIndex = -1;
				}
				else
				{
					int num = selectedIndex;
					int currentDetectIndex = GetCurrentDetectIndex();
					if (currentDetectIndex >= 0 && currentDetectIndex < _job.DetectItems.Count)
					{
						num = FindBindingOptionIndex(_job.DetectItems[currentDetectIndex]);
					}
					if (num < 0)
					{
						num = 0;
					}
					if (num >= cboDetectBindBase.Items.Count)
					{
						num = cboDetectBindBase.Items.Count - 1;
					}
					cboDetectBindBase.SelectedIndex = num;
				}
			}
			finally
			{
				_suppressUiEvents = previousSuppressUiEvents;
			}
			FixComboWidth(cboDetectBindBase, 120);
		}

		private int GetCurrentDetectIndex()
		{
			if (canvas.SelectedRoi.Kind == CanvasRoiKind.Detect && canvas.SelectedRoi.Index >= 0 && canvas.SelectedRoi.Index < _job.DetectItems.Count)
			{
				return canvas.SelectedRoi.Index;
			}
			return -1;
		}

		private int GetCurrentBaseIndex()
		{
			if (canvas.SelectedRoi.Kind == CanvasRoiKind.Base && canvas.SelectedRoi.Index >= 0 && canvas.SelectedRoi.Index < _job.BaseRois.Count)
			{
				return canvas.SelectedRoi.Index;
			}
			return -1;
		}

		private int GetCurrentCircleBaseIndex()
		{
			if ((canvas.SelectedRoi.Kind == CanvasRoiKind.CircleBase1 || canvas.SelectedRoi.Kind == CanvasRoiKind.CircleBase2) && canvas.SelectedRoi.Index >= 0 && canvas.SelectedRoi.Index < _job.CircleBaseRois.Count)
			{
				return canvas.SelectedRoi.Index;
			}
			return -1;
		}

		private int GetBaseCaliperEditorTargetIndex()
		{
			int currentBaseIndex = GetCurrentBaseIndex();
			if (currentBaseIndex >= 0)
			{
				return currentBaseIndex;
			}
			int currentCircleBaseIndex = GetCurrentCircleBaseIndex();
			if (currentCircleBaseIndex >= 0)
			{
				return -1;
			}
			int currentDetectIndex = GetCurrentDetectIndex();
			if (currentDetectIndex >= 0 && currentDetectIndex < _job.DetectItems.Count)
			{
				DetectRoiItem detectRoiItem = _job.DetectItems[currentDetectIndex];
				if (detectRoiItem != null && detectRoiItem.UseReferenceLine)
				{
					int num = ResolveDetectBoundBaseIndex(detectRoiItem);
					if (num >= 0 && num < _job.BaseRois.Count)
					{
						return num;
					}
				}
			}
			return (_job.BaseRois.Count <= 0) ? (-1) : 0;
		}

		private double GetDisplayResolutionMm()
		{
			double num = _job?.PixelResolutionX ?? 1.0;
			double num2 = _job?.PixelResolutionY ?? 1.0;
			if (num <= 0.0 && num2 > 0.0)
			{
				num = num2;
			}
			if (num2 <= 0.0 && num > 0.0)
			{
				num2 = num;
			}
			if (num <= 0.0)
			{
				num = 1.0;
			}
			if (num2 <= 0.0)
			{
				num2 = 1.0;
			}
			return (num + num2) * 0.5;
		}

		private double PxToMm(double px)
		{
			return px * GetDisplayResolutionMm();
		}

		private double MmToPx(double mm)
		{
			double displayResolutionMm = GetDisplayResolutionMm();
			return (displayResolutionMm > 0.0) ? (mm / displayResolutionMm) : mm;
		}

		private int GetCurrentCirclePointIndex()
		{
			if (canvas.SelectedRoi.Kind == CanvasRoiKind.CirclePoint && canvas.SelectedRoi.Index >= 0 && canvas.SelectedRoi.Index < _job.CirclePointRois.Count)
			{
				return canvas.SelectedRoi.Index;
			}
			return -1;
		}

		private int GetCircleBaseCaliperEditorTargetIndex()
		{
			int currentCircleBaseIndex = GetCurrentCircleBaseIndex();
			if (currentCircleBaseIndex >= 0)
			{
				return currentCircleBaseIndex;
			}
			int currentDetectIndex = GetCurrentDetectIndex();
			if (currentDetectIndex >= 0 && currentDetectIndex < _job.DetectItems.Count)
			{
				DetectRoiItem detectRoiItem = _job.DetectItems[currentDetectIndex];
				if (detectRoiItem != null && detectRoiItem.UseReferenceLine && detectRoiItem.ReferenceBaseKind == ReferenceBaseKind.CirclePair)
				{
					int num = ResolveDetectBoundCircleBaseIndex(detectRoiItem);
					if (num >= 0 && num < _job.CircleBaseRois.Count)
					{
						return num;
					}
				}
			}
			return -1;
		}

		private int GetCirclePointCaliperEditorTargetIndex()
		{
			int currentCirclePointIndex = GetCurrentCirclePointIndex();
			if (currentCirclePointIndex >= 0)
			{
				return currentCirclePointIndex;
			}
			int currentDetectIndex = GetCurrentDetectIndex();
			if (currentDetectIndex >= 0 && currentDetectIndex < _job.DetectItems.Count)
			{
				DetectRoiItem detectRoiItem = _job.DetectItems[currentDetectIndex];
				if (detectRoiItem != null && detectRoiItem.ReferenceBaseKind == ReferenceBaseKind.CirclePoint)
				{
					int num = _job.ResolveCirclePointRoiIndex(detectRoiItem.CirclePointRoiId, detectRoiItem.CirclePointRoiIndex);
					if (num >= 0 && num < _job.CirclePointRois.Count)
					{
						return num;
					}
				}
			}
			return -1;
		}

		private int GetDetectCaliperEditorTargetIndex()
		{
			int currentDetectIndex = GetCurrentDetectIndex();
			if (currentDetectIndex >= 0)
			{
				return currentDetectIndex;
			}
			return (_job.DetectItems.Count <= 0) ? (-1) : 0;
		}

		private void UpdateSelectionHint()
		{
			if (lblRoiHint != null)
			{
				lblRoiHint.Text = string.Empty;
				lblRoiHint.Visible = false;
				lblRoiHint.Height = 0;
			}
		}

		private void SelectInitialCanvasTarget()
		{
			if (_job.DetectItems.Count > 0)
			{
				SelectFirstDetectRoi();
			}
			else if (_job.BaseRois.Count > 0)
			{
				canvas.SetSelection(CanvasRoiKind.Base, 0);
			}
			else if (_job.CircleBaseRois.Count > 0)
			{
				canvas.SetSelection(CanvasRoiKind.CircleBase1, 0);
			}
			else if (_job.CirclePointRois.Count > 0)
			{
				canvas.SetSelection(CanvasRoiKind.CirclePoint, 0);
			}
			else if (!_job.TemplateRoi.IsEmpty)
			{
				canvas.SetSelection(CanvasRoiKind.Template, 0);
			}
			else
			{
				canvas.SetSelection(CanvasRoiKind.None, -1);
			}
		}

		private void SelectFirstDetectRoi()
		{
			if (_job.DetectItems.Count > 0)
			{
				canvas.SetSelection(CanvasRoiKind.Detect, 0);
			}
		}

		private void SetDetectEditorEnabled(bool enabled)
		{
			chkDetectEnabled.Enabled = enabled;
			chkUseReferenceLine.Enabled = enabled;
			numNominal.Enabled = enabled;
			cboAngleRef.Enabled = enabled;
			cboDetectBindBase.Enabled = enabled && _baseBindingOptions.Count > 0;
		}

		private void SetBaseCaliperEditorEnabled(bool enabled, bool circleOutwardEnabled = false)
		{
			cboBasePol.Enabled = enabled;
			numBaseMeasures.Enabled = enabled;
			numBaseSigma.Enabled = enabled;
			numBaseTh.Enabled = enabled;
			numBaseOutward.Enabled = enabled && circleOutwardEnabled;
		}

		private void SetDetectCaliperEditorEnabled(bool enabled)
		{
			cboDetPol.Enabled = enabled;
			numDetMeasures.Enabled = enabled;
			numDetSigma.Enabled = enabled;
			numDetTh.Enabled = enabled;
			numDetFitRatio.Enabled = enabled;
		}

		private void UpdateMatchUiState()
		{
			bool enabled = chkEnableMatch.Checked;
			chkEnableSearchRoi.Enabled = enabled;
			numMatchMinScore.Enabled = enabled;
			numMatchAngleStart.Enabled = enabled;
			numMatchAngleExtent.Enabled = enabled;
		}

		private InspectionLanguage LanguageFromUi()
		{
			return cboLanguage != null && cboLanguage.SelectedIndex == 1 ? InspectionLanguage.English : InspectionLanguage.Chinese;
		}

		private void SetLanguageComboSelection(InspectionLanguage language)
		{
			if (cboLanguage == null)
			{
				return;
			}
			if (cboLanguage.Items.Count != 2)
			{
				cboLanguage.Items.Clear();
				cboLanguage.Items.AddRange(new object[2] { "中文", "English" });
			}
			cboLanguage.SelectedIndex = language == InspectionLanguage.English ? 1 : 0;
		}

		private void ApplyLanguageToUi()
		{
			InspectionLanguage language = _job?.Language ?? InspectionLanguage.Chinese;
			bool oldSuppress = _suppressUiEvents;
			_suppressUiEvents = true;
			try
			{
				RefreshLanguageSensitiveCombos(language);
				ApplyLanguageToControl(this, language);
				SetLanguageComboSelection(language);
				canvas.Language = language;
				canvas.Invalidate();
			}
			finally
			{
				_suppressUiEvents = oldSuppress;
			}
		}

		private static void ApplyLanguageToControl(Control control, InspectionLanguage language)
		{
			if (control == null)
			{
				return;
			}
			if (!(control is ComboBox) && !IsStatusToken(control.Text) && !string.IsNullOrEmpty(control.Text))
			{
				control.Text = LocalizedText.Ui(control.Text, language);
			}
			foreach (Control child in control.Controls)
			{
				ApplyLanguageToControl(child, language);
			}
		}

		private static bool IsStatusToken(string text)
		{
			return text == "OK" || text == "NG" || text == "READY" || text == "WAIT";
		}

		private void RefreshLanguageSensitiveCombos(InspectionLanguage language)
		{
			SetComboItems(cboDetectMode, new string[3] { "两者都检测", "只检测毛刺", "只检测凹陷" }, language);
			SetComboItems(cboAngleRef, new string[3] { "平行于关联基准线", "水平", "竖直" }, language);
			SetComboItems(cboBasePol, new string[3] { "白找黑", "黑找白", "任意" }, language);
			SetComboItems(cboDetPol, new string[3] { "白找黑", "黑找白", "任意" }, language);
		}

		private static void SetComboItems(ComboBox combo, string[] chineseItems, InspectionLanguage language)
		{
			if (combo == null || chineseItems == null)
			{
				return;
			}
			int selectedIndex = combo.SelectedIndex;
			combo.Items.Clear();
			foreach (string item in chineseItems)
			{
				combo.Items.Add(LocalizedText.Ui(item, language));
			}
			if (combo.Items.Count > 0)
			{
				combo.SelectedIndex = Math.Max(0, Math.Min(selectedIndex, combo.Items.Count - 1));
			}
		}

		private void UpdateUseReferenceUiState()
		{
			bool flag = GetCurrentDetectIndex() >= 0;
			chkUseReferenceLine.Enabled = flag;
			bool flag2 = flag;
			cboDetectBindBase.Enabled = flag2 && cboDetectBindBase.Items.Count > 0;
		}

		private void ClearResultVisualsBecauseRoiChanged()
		{
			_hasUnsavedChanges = true;
			if (_resultValid)
			{
				_resultValid = false;
				canvas.ClearOverlays();
				canvas.ClearHud();
				canvas.ClearRuntimeRois();
				canvas.ShowRuntimeRois = false;
				canvas.ShowRois = true;
				canvas.Invalidate();
				lblStatus.Text = "WAIT";
				lblStatus.BackColor = Color.LightGray;
				lblSummary.Text = ((_savedJob != null) ? "ROI/参数已修改。运行会使用当前界面最新参数；保存(示教)会更新当前 Job。" : "ROI/参数已修改。可直接运行预览，或点击 保存(示教) / 确认配置。");
				lblSummary.ForeColor = Color.FromArgb(90, 90, 90);
				lblResultTip.Text = "运行前会自动按当前 ROI 和参数重新生成本次检测配置";
				FixMultiLineLabel(lblSummary, 10);
				FixMultiLineLabel(lblResultTip, 10);
			}
		}

		private bool ConfirmSaveBeforeClose()
		{
			if (!_hasUnsavedChanges)
			{
				return true;
			}
			DialogResult result = MessageBox.Show(this, "参数已修改，是否保存后再关闭？", "保存参数", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
			if (result == DialogResult.Cancel)
			{
				return false;
			}
			if (result == DialogResult.No)
			{
				return true;
			}
			try
			{
				_savedJob = BuildJobForReturn();
				_job = _savedJob.DeepClone();
				ReturnedJob = _savedJob.DeepClone();
				_hasUnsavedChanges = false;
				return true;
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				return false;
			}
		}

		private void LoadImage()
		{
			using (OpenFileDialog openFileDialog = new OpenFileDialog
			{
				Filter = "Images|*.bmp;*.jpg;*.png;*.tif;*.tiff"
			})
			{
				if (openFileDialog.ShowDialog() != DialogResult.OK)
				{
					return;
				}
				using (Bitmap original = new Bitmap(openFileDialog.FileName))
				{
					_src?.Dispose();
					_src = new Bitmap(original);
				}
			}
			canvas.LoadBitmap(_src);
			canvas.FitToWindow();
			canvas.ShowRois = true;
			canvas.ShowRuntimeRois = false;
			canvas.ClearRuntimeRois();
			_suppressRoiChanged = true;
			try
			{
				if (_job.Match.Enabled && _job.TemplateRoi.IsEmpty)
				{
					_job.TemplateRoi = CreateDefaultTemplateRoi();
				}
				if (_job.Match.Enabled && _job.TemplateSearchRoi.IsEmpty && !_job.TemplateRoi.IsEmpty)
				{
					_job.TemplateSearchRoi = CreateDefaultTemplateSearchRoi(_job.TemplateRoi);
				}
				ResolveEditTransformForCurrentImage();
				SetCanvasRoisFromJob();
			}
			finally
			{
				_suppressRoiChanged = false;
			}
			SelectInitialCanvasTarget();
			_resultValid = false;
			SetReady(_editTransformResolved ? "READY" : "模板未匹配", _editTransformResolved ? (_job.Match.Enabled ? "图片已加载，ROI 已按当前模板姿态显示。" : "图片已加载：模板匹配关闭。") : "当前图像未找到模板，已锁定 ROI 编辑以保护模板参考坐标。");
			RequestImportedDisplayRefresh();
		}

		private void AddBaseRoi()
		{
			int count = _job.BaseRois.Count;
			RotRectF roi = CreateDefaultBaseRoi(count);
			_suppressRoiChanged = true;
			try
			{
				canvas.AddBaseRoi(roi);
			}
			finally
			{
				_suppressRoiChanged = false;
			}
			PullCanvasRoisToJob();
			canvas.ShowRois = true;
			canvas.ShowRuntimeRois = false;
			canvas.ClearRuntimeRois();
			canvas.Invalidate();
			if (count >= 0 && count < _job.BaseRois.Count)
			{
				_job.BaseRois[count].Name = $"基准{count + 1}";
				if (_job.BaseRois[count].Caliper == null)
				{
					_job.BaseRois[count].Caliper = _job.BaseCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultBaseCaliper();
				}
			}
			NormalizeDetectBaseBindings();
			RefreshBaseBindingCombo();
			LoadSelectedDetectToUi();
			LoadCurrentBaseCaliperToUi();
			LoadCurrentDetectCaliperToUi();
			ClearResultVisualsBecauseRoiChanged();
			SetReady("READY", $"已添加基准ROI {count + 1}。该基准ROI拥有独立卡尺参数。");
		}

		private void AddCircleBaseRoi()
		{
			int count = _job.CircleBaseRois.Count;
			CircleBaseRoiPair pair = CreateDefaultCircleBaseRoi(count);
			_suppressRoiChanged = true;
			try
			{
				canvas.AddCircleBaseRoi(pair);
			}
			finally
			{
				_suppressRoiChanged = false;
			}
			PullCanvasRoisToJob();
			canvas.ShowRois = true;
			canvas.ShowRuntimeRois = false;
			canvas.ClearRuntimeRois();
			canvas.Invalidate();
			if (count >= 0 && count < _job.CircleBaseRois.Count)
			{
				_job.CircleBaseRois[count].Name = $"圆基准{count + 1}";
				if (_job.CircleBaseRois[count].Caliper == null)
				{
					_job.CircleBaseRois[count].Caliper = _job.CircleCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultCircleCaliper();
				}
			}
			NormalizeDetectBaseBindings();
			RefreshBaseBindingCombo();
			LoadSelectedDetectToUi();
			LoadCurrentBaseCaliperToUi();
			LoadCurrentDetectCaliperToUi();
			ClearResultVisualsBecauseRoiChanged();
			SetReady("READY", $"已添加圆基准ROI {count + 1}。拖动两个圆覆盖目标圆，运行时会用两个圆心连线作为基准线。");
		}

		private void AddCirclePointRoi()
		{
			int count = _job.CirclePointRois.Count;
			CircleRoiF roi = CreateDefaultCirclePointRoi(count);
			_suppressRoiChanged = true;
			try
			{
				canvas.AddCirclePointRoi(roi);
			}
			finally
			{
				_suppressRoiChanged = false;
			}
			PullCanvasRoisToJob();
			canvas.ShowRois = true;
			canvas.ShowRuntimeRois = false;
			canvas.ClearRuntimeRois();
			canvas.Invalidate();
			if (count >= 0 && count < _job.CirclePointRois.Count)
			{
				_job.CirclePointRois[count].Name = $"圆点基准{count + 1}";
				if (_job.CirclePointRois[count].Caliper == null)
				{
					_job.CirclePointRois[count].Caliper = _job.CircleCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultCircleCaliper();
				}
			}
			NormalizeDetectBaseBindings();
			RefreshBaseBindingCombo();
			LoadSelectedDetectToUi();
			LoadCurrentBaseCaliperToUi();
			LoadCurrentDetectCaliperToUi();
			ClearResultVisualsBecauseRoiChanged();
			SetReady("READY", $"已添加圆点基准ROI {count + 1}。第四种检测会用它找圆心，再计算圆心到切割边拟合线的垂直距离。");
		}

		private void AddDetectRoi()
		{
			int count = _job.DetectItems.Count;
			RotRectF roi = CreateDefaultDetectRoi(count);
			_suppressRoiChanged = true;
			try
			{
				canvas.AddDetectRoi(roi);
			}
			finally
			{
				_suppressRoiChanged = false;
			}
			PullCanvasRoisToJob();
			canvas.ShowRois = true;
			canvas.ShowRuntimeRois = false;
			canvas.ClearRuntimeRois();
			canvas.Invalidate();
			if (count >= 0 && count < _job.DetectItems.Count)
			{
				_job.DetectItems[count].Name = $"检测{count + 1}";
				_job.DetectItems[count].Enabled = true;
				_job.DetectItems[count].UseReferenceLine = chkUseReferenceLine.Checked;
				_job.DetectItems[count].AngleReference = DetectAngleReferenceMode.ParallelToBase;
				if (_job.CirclePointRois.Count > 0)
				{
					_job.DetectItems[count].ReferenceBaseKind = ReferenceBaseKind.CirclePoint;
					_job.DetectItems[count].UseReferenceLine = false;
					_job.DetectItems[count].CirclePointRoiIndex = 0;
					_job.DetectItems[count].CirclePointRoiId = _job.CirclePointRois[0]?.Id ?? "";
				}
				else if (_job.BaseRois.Count > 0)
				{
					_job.DetectItems[count].ReferenceBaseKind = ReferenceBaseKind.LineRoi;
					_job.DetectItems[count].BaseRoiIndex = 0;
					_job.DetectItems[count].BaseRoiId = _job.BaseRois[0]?.Id ?? "";
				}
				else if (_job.CircleBaseRois.Count > 0)
				{
					_job.DetectItems[count].ReferenceBaseKind = ReferenceBaseKind.CirclePair;
					_job.DetectItems[count].CircleBaseRoiIndex = 0;
					_job.DetectItems[count].CircleBaseRoiId = _job.CircleBaseRois[0]?.Id ?? "";
				}
				_job.DetectItems[count].NominalDistancePx = 0.0;
				_job.DetectItems[count].BurrTolerancePx = 2.0;
				if (_job.DetectItems[count].Caliper == null)
				{
					_job.DetectItems[count].Caliper = _job.DetectCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultDetectCaliper();
				}
			}
			_job.UseReferenceLine = _job.DetectItems[count].UseReferenceLine;
			RefreshBaseBindingCombo();
			LoadSelectedDetectToUi();
			LoadCurrentBaseCaliperToUi();
			LoadCurrentDetectCaliperToUi();
			ClearResultVisualsBecauseRoiChanged();
			SetReady("READY", $"已添加检测ROI {count + 1}。该检测ROI拥有独立卡尺参数。");
		}

		private RotRectF CreateDefaultTemplateRoi()
		{
			if (_src == null)
			{
				return default(RotRectF);
			}
			int num = _src.Width;
			int num2 = _src.Height;
			return RotRectF.FromAxisAligned(new RectangleF((float)num * 0.08f, (float)num2 * 0.1f, (float)num * 0.2f, (float)num2 * 0.2f));
		}

		private RotRectF CreateDefaultTemplateSearchRoi(RotRectF templateRoi)
		{
			if (_src == null || templateRoi.IsEmpty)
			{
				return default(RotRectF);
			}
			RectangleF bounds = templateRoi.BoundingBox();
			float imageWidth = _src.Width;
			float imageHeight = _src.Height;
			float searchWidth = Math.Min(imageWidth, Math.Max(bounds.Width, Math.Min(imageWidth * 0.8f, Math.Max(bounds.Width * 2f, bounds.Width + 64f))));
			float searchHeight = Math.Min(imageHeight, Math.Max(bounds.Height, Math.Min(imageHeight * 0.8f, Math.Max(bounds.Height * 2f, bounds.Height + 64f))));
			float left = Math.Max(0f, Math.Min(bounds.Left + bounds.Width * 0.5f - searchWidth * 0.5f, imageWidth - searchWidth));
			float top = Math.Max(0f, Math.Min(bounds.Top + bounds.Height * 0.5f - searchHeight * 0.5f, imageHeight - searchHeight));
			RectangleF search = new RectangleF(left, top, searchWidth, searchHeight);
			return search.Width > 2f && search.Height > 2f ? RotRectF.FromAxisAligned(search) : default(RotRectF);
		}

		private RotRectF CreateDefaultBaseRoi(int index)
		{
			int num = _src.Width;
			int num2 = _src.Height;
			float num3 = (float)num * 0.14f;
			float num4 = (float)num2 * 0.18f;
			int num5 = 3;
			int num6 = index / num5;
			int num7 = index % num5;
			float num8 = (float)num * 0.5f;
			float num9 = (float)num2 * 0.1f;
			float num10 = (float)num * 0.04f;
			float num11 = (float)num2 * 0.05f;
			float num12 = num8 + (float)num7 * (num3 + num10);
			float num13 = num9 + (float)num6 * (num4 + num11);
			if (num12 + num3 > (float)(num - 10))
			{
				num12 = (float)num - num3 - 10f;
			}
			if (num13 + num4 > (float)(num2 - 10))
			{
				num13 = (float)num2 - num4 - 10f;
			}
			return RotRectF.FromAxisAligned(new RectangleF(num12, num13, num3, num4));
		}

		private RotRectF CreateDefaultDetectRoi(int index)
		{
			int num = _src.Width;
			int num2 = _src.Height;
			float num3 = (float)num * 0.16f;
			float num4 = (float)num2 * 0.2f;
			int num5 = 3;
			int num6 = index / num5;
			int num7 = index % num5;
			float num8 = (float)num * 0.08f + (float)num7 * (num3 + (float)num * 0.06f);
			float num9 = (float)num2 * 0.55f + (float)num6 * (num4 + (float)num2 * 0.06f);
			if (num8 + num3 > (float)(num - 10))
			{
				num8 = (float)num - num3 - 10f;
			}
			if (num9 + num4 > (float)(num2 - 10))
			{
				num9 = (float)num2 - num4 - 10f;
			}
			return RotRectF.FromAxisAligned(new RectangleF(num8, num9, num3, num4));
		}

		private CircleBaseRoiPair CreateDefaultCircleBaseRoi(int index)
		{
			int num = _src.Width;
			int num2 = _src.Height;
			float radius = Math.Max(8f, (float)Math.Min(num, num2) * 0.055f);
			float num3 = (float)num2 * (0.28f + (float)(index % 3) * 0.12f);
			float num4 = (float)num * 0.3f;
			float num5 = (float)num * 0.48f;
			return new CircleBaseRoiPair
			{
				Circle1 = new CircleRoiF(new PointF(num4, num3), radius),
				Circle2 = new CircleRoiF(new PointF(num5, num3), radius)
			};
		}

		private CircleRoiF CreateDefaultCirclePointRoi(int index)
		{
			int num = _src.Width;
			int num2 = _src.Height;
			float num3 = Math.Max(8f, (float)Math.Min(num, num2) * 0.045f);
			int num4 = 4;
			int num5 = index / num4;
			int num6 = index % num4;
			float val = (float)num * (0.18f + (float)num6 * 0.14f);
			float val2 = (float)num2 * (0.28f + (float)num5 * 0.14f);
			val = Math.Max(num3 + 4f, Math.Min((float)num - num3 - 4f, val));
			val2 = Math.Max(num3 + 4f, Math.Min((float)num2 - num3 - 4f, val2));
			return new CircleRoiF(new PointF(val, val2), num3);
		}

		private void ApplyAutoCaliperGeometry(EdgeInspectJob job)
		{
			if (job == null)
			{
				return;
			}
			foreach (BaseRoiItem item in job.BaseRois.Where((BaseRoiItem x) => x != null && !x.Roi.IsEmpty))
			{
				if (item.Caliper == null)
				{
					item.Caliper = job.BaseCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultBaseCaliper();
				}
				AutoFillCaliperFromRotRoi(item.Roi, item.Caliper);
			}
			foreach (CircleBaseRoiItem item2 in job.CircleBaseRois.Where((CircleBaseRoiItem x) => x != null && !x.Circle1.IsEmpty && !x.Circle2.IsEmpty))
			{
				if (item2.Caliper == null)
				{
					item2.Caliper = job.CircleCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultCircleCaliper();
				}
				AutoFillCircleCaliperFromCircleRoi(item2.Circle1, item2.Caliper);
			}
			foreach (CirclePointRoiItem item3 in job.CirclePointRois.Where((CirclePointRoiItem x) => x != null && !x.Circle.IsEmpty))
			{
				if (item3.Caliper == null)
				{
					item3.Caliper = job.CircleCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultCircleCaliper();
				}
				AutoFillCircleCaliperFromCircleRoi(item3.Circle, item3.Caliper);
			}
			foreach (DetectRoiItem item4 in job.DetectItems.Where((DetectRoiItem x) => x != null && x.Enabled && !x.Roi.IsEmpty))
			{
				if (item4.Caliper == null)
				{
					item4.Caliper = job.DetectCaliper?.DeepClone() ?? EdgeInspectJob.CreateDefaultDetectCaliper();
				}
				AutoFillCaliperFromRotRoi(item4.Roi, item4.Caliper);
			}
			BaseRoiItem baseRoiItem = job.BaseRois.FirstOrDefault((BaseRoiItem x) => x != null && !x.Roi.IsEmpty);
			if (baseRoiItem?.Caliper != null)
			{
				job.BaseCaliper = baseRoiItem.Caliper.DeepClone();
			}
			CircleBaseRoiItem circleBaseRoiItem = job.CircleBaseRois.FirstOrDefault((CircleBaseRoiItem x) => x != null && !x.Circle1.IsEmpty && !x.Circle2.IsEmpty);
			if (circleBaseRoiItem?.Caliper != null)
			{
				job.CircleCaliper = circleBaseRoiItem.Caliper.DeepClone();
			}
			CirclePointRoiItem circlePointRoiItem = job.CirclePointRois.FirstOrDefault((CirclePointRoiItem x) => x != null && !x.Circle.IsEmpty);
			if (circlePointRoiItem?.Caliper != null)
			{
				job.CircleCaliper = circlePointRoiItem.Caliper.DeepClone();
			}
			DetectRoiItem detectRoiItem = job.DetectItems.FirstOrDefault((DetectRoiItem x) => x != null && x.Enabled && !x.Roi.IsEmpty);
			if (detectRoiItem?.Caliper != null)
			{
				job.DetectCaliper = detectRoiItem.Caliper.DeepClone();
			}
		}

		private static void AutoFillCaliperFromRotRoi(RotRectF rr, CaliperParameters cp)
		{
			if (!rr.IsEmpty && cp != null)
			{
				int num = Math.Max(1, cp.NumMeasures);
				cp.MeasureLength = Math.Max(1.0, Math.Min(2000.0, rr.HalfLen2));
				cp.MeasureWidth = Math.Max(1.0, Math.Min(2000.0, rr.HalfLen1 / (float)num));
			}
		}

		private static void AutoFillCircleCaliperFromCircleRoi(CircleRoiF circle, CaliperParameters cp)
		{
			if (!circle.IsEmpty && cp != null)
			{
				int num = Math.Max(8, cp.NumMeasures);
				double num2 = Math.PI * 2.0 * (double)circle.Radius;
				cp.MeasureLength = Math.Max(1.0, Math.Min(2000.0, (double)circle.Radius * 0.18));
				cp.MeasureWidth = Math.Max(1.0, Math.Min(2000.0, num2 / (double)num * 0.35));
				if (cp.SearchOutward < 0.0)
				{
					cp.SearchOutward = 0.0;
				}
			}
		}

		private DefectDetectMode DetectModeFromUi()
		{
			switch (cboDetectMode.SelectedIndex)
			{
			case 1:
				return DefectDetectMode.BurrOnly;
			case 2:
				return DefectDetectMode.DentOnly;
			default:
				return DefectDetectMode.Both;
			}
		}

		private static int DetectModeToIndex(DefectDetectMode mode)
		{
			switch (mode)
			{
			case DefectDetectMode.BurrOnly:
				return 1;
			case DefectDetectMode.DentOnly:
				return 2;
			default:
				return 0;
			}
		}

		private DetectAngleReferenceMode AngleRefFromUi()
		{
			switch (cboAngleRef.SelectedIndex)
			{
			case 1:
				return DetectAngleReferenceMode.Horizontal;
			case 2:
				return DetectAngleReferenceMode.Vertical;
			default:
				return DetectAngleReferenceMode.ParallelToBase;
			}
		}

		private static string DetectModeToText(DefectDetectMode mode)
		{
			switch (mode)
			{
			case DefectDetectMode.BurrOnly:
				return "只检测毛刺";
			case DefectDetectMode.DentOnly:
				return "只检测凹陷";
			default:
				return "毛刺+凹陷";
			}
		}

		private static bool RequiresAnyReferenceBase(EdgeInspectJob job)
		{
			return job != null && job.DetectItems != null && job.DetectItems.Any((DetectRoiItem x) => x != null && x.Enabled && !x.Roi.IsEmpty && x.UseReferenceLine);
		}

		private static string GetJudgeModeText(bool hasReferenceLineItems, bool hasSelfFitItems)
		{
			if (hasReferenceLineItems && hasSelfFitItems)
			{
				return "混合判定";
			}
			if (hasReferenceLineItems)
			{
				return "基准线判定";
			}
			return "自拟合线判定";
		}

		private static string GetJudgeModeText(EdgeInspectResult result)
		{
			if (result == null)
			{
				return "未知";
			}
			bool flag = result.HasReferenceLineItems;
			bool flag2 = result.HasSelfFitLineItems;
			if (!flag && !flag2 && result.DetectResults != null && result.DetectResults.Count > 0)
			{
				flag = result.DetectResults.Any((DetectRoiInspectResult x) => x?.UseReferenceLine ?? false);
				flag2 = result.DetectResults.Any((DetectRoiInspectResult x) => x != null && !x.UseReferenceLine);
			}
			return GetJudgeModeText(flag, flag2);
		}

		private static string PolToTransition(int idx)
		{
			switch (idx)
			{
			case 0:
				return CaliperParameters.NegativeTransition;
			case 1:
				return CaliperParameters.PositiveTransition;
			case 2:
				return CaliperParameters.AllTransition;
			default:
				return null;
			}
		}

		private static int TransitionToPolIndex(string transition)
		{
			string normalized;
			if (!CaliperParameters.TryNormalizeTransition(transition, out normalized))
			{
				return 0;
			}
			if (normalized == CaliperParameters.PositiveTransition)
			{
				return 1;
			}
			if (normalized == CaliperParameters.AllTransition)
			{
				return 2;
			}
			return 0;
		}

		private static decimal ClampDecimal(decimal value, NumericUpDown num)
		{
			if (value < num.Minimum)
			{
				return num.Minimum;
			}
			if (value > num.Maximum)
			{
				return num.Maximum;
			}
			return value;
		}

		private static string LocalizedRoiName(string name, InspectionLanguage language)
		{
			return LocalizedText.Message(name ?? "", language);
		}

		private void ShowResult(EdgeInspectResult r)
		{
			if (r == null)
			{
				SetError("错误", "结果为空");
				return;
			}
			InspectionLanguage language = r.Language;
			canvas.Language = language;
			bool flag = r.HasReferenceLineItems || (r.DetectResults != null && r.DetectResults.Any((DetectRoiInspectResult x) => x?.UseReferenceLine ?? false));
			canvas.ClearRuntimeRois();
			canvas.SetRuntimeRois(r.TemplateRoiCur, flag ? r.BaseResults.Select((BaseRoiInspectResult x) => x.RoiCur) : Enumerable.Empty<RotRectF>(), flag ? r.CircleBaseResults.Select((CircleBaseRoiInspectResult x) => new CircleBaseRoiPair
			{
				Circle1 = x.Circle1RoiCur,
				Circle2 = x.Circle2RoiCur
			}) : Enumerable.Empty<CircleBaseRoiPair>(), Enumerable.Empty<CircleRoiF>(), r.DetectResults.Select((DetectRoiInspectResult x) => x.RoiCur));
			canvas.SetRuntimeTemplateSearchRoi(r.TemplateSearchRoiCur);
			canvas.ShowRuntimeRois = true;
			canvas.ShowRois = false;
			string text = ((!r.TemplateMatchEnabled) ? LocalizedText.Message("关闭", language) : (r.TemplateMatchOk ? $"OK({r.TemplateMatchScore:0.00})" : "NG"));
			string text2 = LocalizedText.Message(DetectModeToText(r.DetectMode), language);
			string judgeModeText = LocalizedText.Message(GetJudgeModeText(r), language);
			if (r.TemplateMatchEnabled && !r.TemplateMatchOk)
			{
				lblStatus.Text = "NG";
				lblStatus.BackColor = Color.Red;
				lblSummary.Text = LocalizedText.Message("模板匹配: NG", language);
				lblSummary.ForeColor = Color.FromArgb(210, 40, 40);
				lblResultTip.Text = LocalizedText.Message("请检查模板 ROI、最小分数、角度范围和图像质量", language);
				FixMultiLineLabel(lblSummary, 10);
				FixMultiLineLabel(lblResultTip, 10);
				canvas.ClearOverlays();
				canvas.HudTextColor = Color.Red;
				canvas.HudText = LocalizedText.Message("模板匹配: NG\n请检查：模板ROI/最小分数/角度范围/图像质量", language);
				canvas.Invalidate();
				return;
			}
			int num = r.DetectResults.Count((DetectRoiInspectResult x) => !x.Success);
			List<string> list = (from x in r.DetectResults
				where !x.Success
				select LocalizedRoiName(x.Name, language) + ": " + x.Message).Take(6).ToList();
			lblStatus.Text = (r.Success ? "OK" : "NG");
			lblStatus.BackColor = (r.Success ? Color.LimeGreen : Color.Red);
			lblSummary.Text = LocalizedText.Message($"模式 {text2} | 判定 {judgeModeText} | 模板匹配 {text}\n检测 {r.DetectResults.Count} | 失败 {num} | 毛刺 {r.BurrCount} | 凹陷 {r.DentCount} | 超边 {r.OverEdgeCount} | 漏铜 {r.CopperLeakCount}", language);
			lblSummary.ForeColor = (r.Success ? Color.FromArgb(0, 140, 60) : Color.FromArgb(210, 40, 40));
			lblResultTip.Text = LocalizedText.Message((list.Count > 0) ? string.Join("；", list) : $"Δ最小 {r.DeltaMin:F2}px | Δ最大 {r.DeltaMax:F2}px | Δ平均 {r.DeltaMean:F2}px", language);
			FixMultiLineLabel(lblSummary, 10);
			FixMultiLineLabel(lblResultTip, 10);
			List<string> list2 = new List<string>();
			list2.Add(LocalizedText.Message("检测模式: " + text2, language));
			list2.Add(LocalizedText.Message("判定方式: " + judgeModeText, language));
			list2.Add(LocalizedText.Message("模板匹配: " + text, language));
			list2.Add(LocalizedText.Message("结果: " + (r.Success ? "OK" : "NG"), language));
			list2.Add(LocalizedText.Message($"线基准: {r.BaseResults.Count}    圆基准: {r.CircleBaseResults.Count}    圆点: {r.CirclePointResults.Count}    检测ROI: {r.DetectResults.Count}", language));
			list2.Add(LocalizedText.Message($"失败ROI: {num}    毛刺: {r.BurrCount}    凹陷: {r.DentCount}    超边: {r.OverEdgeCount}    漏铜: {r.CopperLeakCount}", language));
			list2.Add(LocalizedText.Message($"局部Δ 最小/最大/平均: {r.DeltaMin:F2} / {r.DeltaMax:F2} / {r.DeltaMean:F2}", language));
			List<string> list3 = list2;
			foreach (DetectRoiInspectResult item in r.DetectResults)
			{
				string displayName = LocalizedRoiName(item.Name, language);
				string arg = (double.IsNaN(item.AngleDeltaDeg) ? "N/A" : $"{item.AngleDeltaDeg:F3}°");
				if (item.Success)
				{
					if (item.HasOverallDistance)
					{
						list3.Add(LocalizedText.Message($"{displayName}: OK | 整体={item.OverallDistanceValue:F4}mm Δ={item.OverallDeltaValue:+0.0000;-0.0000;0.0000}mm | 像素={item.OverallDistancePx:F2}px", language));
					}
					else
					{
						list3.Add(LocalizedText.Message($"{displayName}: OK | 标准={PxToMm(item.NominalDistancePx):F4}mm | 角差={arg}", language));
					}
				}
				else
				{
					list3.Add(LocalizedText.Message(displayName + ": NG | " + item.Message, language));
				}
			}
			canvas.HudTextColor = (r.Success ? Color.Lime : Color.Red);
			canvas.HudText = string.Join("\n", list3);
			canvas.ClearOverlays();
			DrawTemplateMatchOverlay(r);
			if (flag)
			{
				for (int num2 = 0; num2 < r.BaseResults.Count; num2++)
				{
					BaseRoiInspectResult baseRoiInspectResult = r.BaseResults[num2];
					if (baseRoiInspectResult.Line != null && baseRoiInspectResult.Line.Success)
					{
						Color color = ((num2 == 0) ? Color.Cyan : Color.Orange);
						canvas.OverlayLines.Add(new ColoredPolyline
						{
							Color = color,
							Width = 2f,
							Arrow = false,
							Points = new List<PointF> { baseRoiInspectResult.Line.P1, baseRoiInspectResult.Line.P2 }
						});
					}
				}
				for (int num3 = 0; num3 < r.CircleBaseResults.Count; num3++)
				{
					CircleBaseRoiInspectResult circleBaseRoiInspectResult = r.CircleBaseResults[num3];
					DrawCircleFitOverlay(circleBaseRoiInspectResult.Circle1, Color.Cyan, circleBaseRoiInspectResult.Name + "-圆1");
					DrawCircleFitOverlay(circleBaseRoiInspectResult.Circle2, Color.Cyan, circleBaseRoiInspectResult.Name + "-圆2");
					if (circleBaseRoiInspectResult.Line != null && circleBaseRoiInspectResult.Line.Success)
					{
						canvas.OverlayLines.Add(new ColoredPolyline
						{
							Color = Color.Cyan,
							Width = 2f,
							Arrow = false,
							Points = new List<PointF> { circleBaseRoiInspectResult.Line.P1, circleBaseRoiInspectResult.Line.P2 }
						});
					}
				}
			}
			foreach (CirclePointRoiInspectResult circlePointResult in r.CirclePointResults)
			{
				DrawCircleFitOverlay(circlePointResult.Circle, Color.DeepSkyBlue, circlePointResult.Name);
			}
			foreach (DetectRoiInspectResult item2 in r.DetectResults.Where((DetectRoiInspectResult x) => x != null))
			{
				if (item2.FittedLine != null && item2.FittedLine.Success)
				{
					if (TryGetLocalFitSegment(item2.FittedLine, item2.RoiCur, out var p3, out var p4))
					{
						canvas.OverlayLines.Add(new ColoredPolyline
						{
							Color = Color.DeepSkyBlue,
							Width = 2f,
							Arrow = false,
							Points = new List<PointF> { p3, p4 }
						});
					}
				}
			}
			foreach (DetectRoiInspectResult item3 in r.DetectResults.Where((DetectRoiInspectResult x) => !x.Success))
			{
				PointF[] corners = item3.RoiCur.GetCorners();
				if (corners != null && corners.Length >= 4)
				{
					List<PointF> points = new List<PointF>(corners) { corners[0] };
					canvas.OverlayLines.Add(new ColoredPolyline
					{
						Color = Color.Red,
						Width = 3f,
						Arrow = false,
						Points = points
					});
					canvas.OverlayPoints.Add(new SolidPoint
					{
						Center = item3.RoiCur.Center,
						Radius = 4f,
						Color = Color.Red,
						Label = item3.Name + " NG",
						LabelPriority = 120
					});
				}
			}
			foreach (DetectRoiInspectResult detectResult in r.DetectResults)
			{
				if (detectResult.Points == null || detectResult.Points.Count == 0)
				{
					continue;
				}
				int maxDentIndex = -1;
				int maxBurrIndex = -1;
				double maxDentDelta = 0.0;
				double maxBurrDelta = 0.0;
				for (int num6 = 0; num6 < detectResult.Points.Count; num6++)
				{
					double deltaPx = detectResult.Points[num6].DeltaPx;
					if (deltaPx > maxDentDelta)
					{
						maxDentDelta = deltaPx;
						maxDentIndex = num6;
					}
					if (deltaPx < maxBurrDelta)
					{
						maxBurrDelta = deltaPx;
						maxBurrIndex = num6;
					}
				}
				int num7 = Math.Max(4, (detectResult.Points.Count + 4) / 5);
				for (int num8 = 0; num8 < detectResult.Points.Count; num8++)
				{
					EdgePointResult edgePointResult = detectResult.Points[num8];
					Color color2 = ((edgePointResult.IsBurr || edgePointResult.IsDent) ? Color.Red : Color.Lime);
					bool flag2 = num8 == maxDentIndex || num8 == maxBurrIndex;
					string label = (!flag2 && num8 % num7 == 0) ? $"{detectResult.Name} P{num8 + 1} Δ={edgePointResult.DeltaValue:+0.0000;-0.0000;0.0000}mm" : null;
					canvas.OverlayPoints.Add(new SolidPoint
					{
						Center = edgePointResult.Point,
						Radius = (flag2 ? 4 : 3),
						Color = color2,
						Label = label,
						LabelPriority = 10,
						IsAbnormal = edgePointResult.IsBurr || edgePointResult.IsDent
					});
				}
				if (detectResult.HasOverallDistance)
				{
					DrawOverallDistance(detectResult);
				}
				if (detectResult.FittedLine != null && detectResult.FittedLine.Success)
				{
					Color maxBurrColor = (maxBurrIndex >= 0 && detectResult.Points[maxBurrIndex].IsBurr) ? Color.Red : Color.Orange;
					Color maxDentColor = (maxDentIndex >= 0 && detectResult.Points[maxDentIndex].IsDent) ? Color.Red : Color.Gold;
					DrawLocalExtreme(detectResult, maxBurrIndex, "最大毛刺", maxBurrColor);
					DrawLocalExtreme(detectResult, maxDentIndex, "最大凹陷", maxDentColor);
				}
			}
			canvas.Invalidate();
		}

		private void DrawOverallDistance(DetectRoiInspectResult dr)
		{
			if (dr != null && dr.HasOverallDistance)
			{
				PointF overallMeasurePoint = dr.OverallMeasurePoint;
				PointF overallFootPoint = dr.OverallFootPoint;
				canvas.OverlayPoints.Add(new SolidPoint
				{
					Center = overallMeasurePoint,
					Radius = 5f,
					Color = Color.DeepSkyBlue,
					Label = null
				});
				canvas.OverlayLines.Add(new ColoredPolyline
				{
					Color = Color.DeepSkyBlue,
					Width = 2f,
					Arrow = false,
					Points = new List<PointF> { overallMeasurePoint, overallFootPoint }
				});
				PointF center = new PointF((overallMeasurePoint.X + overallFootPoint.X) * 0.5f, (overallMeasurePoint.Y + overallFootPoint.Y) * 0.5f);
				canvas.OverlayPoints.Add(new SolidPoint
				{
					Center = center,
					Radius = 0f,
					Color = Color.DeepSkyBlue,
					Label = $"{dr.Name} 整体={dr.OverallDistanceValue:0.0000}mm Δ={dr.OverallDeltaValue:+0.0000;-0.0000;0.0000}mm",
					LabelPriority = 110
				});
			}
		}

		private void DrawLocalExtreme(DetectRoiInspectResult dr, int idx, string tag, Color lineColor)
		{
			if (dr != null && idx >= 0 && idx < dr.Points.Count)
			{
				EdgeLineFit judgeLine = (dr.UseReferenceLine && dr.JudgeLine != null && dr.JudgeLine.Success) ? dr.JudgeLine : dr.FittedLine;
				if (judgeLine == null || !judgeLine.Success)
				{
					return;
				}
				EdgePointResult edgePointResult = dr.Points[idx];
				PointF point = edgePointResult.Point;
				PointF item = ProjectPointToLine(point, judgeLine.P1, judgeLine.P2);
				canvas.OverlayLines.Add(new ColoredPolyline
				{
					Color = lineColor,
					Width = 2f,
					Arrow = false,
					Points = new List<PointF> { point, item }
				});
				PointF center = new PointF((point.X + item.X) * 0.5f, (point.Y + item.Y) * 0.5f);
				canvas.OverlayPoints.Add(new SolidPoint
				{
					Center = center,
					Radius = 0f,
					Color = lineColor,
					Label = $"{dr.Name} {tag}={Math.Abs(edgePointResult.DeltaValue):0.0000}mm",
					LabelPriority = 110
				});
			}
		}

		private void DrawCircleFitOverlay(EdgeCircleFit circle, Color color, string label)
		{
			if (circle != null && circle.Success && !(circle.Radius <= 0.0))
			{
				List<PointF> list = new List<PointF>(97);
				for (int i = 0; i <= 96; i++)
				{
					double num = (double)i * Math.PI * 2.0 / 96.0;
					list.Add(new PointF((float)((double)circle.Center.X + Math.Cos(num) * circle.Radius), (float)((double)circle.Center.Y + Math.Sin(num) * circle.Radius)));
				}
				canvas.OverlayLines.Add(new ColoredPolyline
				{
					Color = color,
					Width = 2f,
					Arrow = false,
					Points = list
				});
				canvas.OverlayPoints.Add(new SolidPoint
				{
					Center = circle.Center,
					Radius = 4f,
					Color = color,
					Label = label,
					LabelPriority = 80
				});
			}
		}

		private void DrawTemplateMatchOverlay(EdgeInspectResult r)
		{
			if (r == null || !r.TemplateMatchEnabled || !r.TemplateMatchOk)
			{
				return;
			}
			Color contourColor = Color.FromArgb(255, 80, 220, 255);
			List<PointF> segment = new List<PointF>();
			Action flushSegment = delegate
			{
				if (segment.Count >= 2)
				{
					canvas.OverlayLines.Add(new ColoredPolyline
					{
						Points = new List<PointF>(segment),
						Color = contourColor,
						Width = 1.8f,
						Arrow = false
					});
				}
				segment.Clear();
			};
			foreach (PointF pt in r.TemplateMatchContourPoints)
			{
				if (IsContourSeparator(pt))
				{
					flushSegment();
				}
				else
				{
					segment.Add(pt);
				}
			}
			flushSegment();

			PointF center = r.TemplateMatchCenter;
			const float cross = 14f;
			canvas.OverlayLines.Add(new ColoredPolyline
			{
				Points = new List<PointF>
				{
					new PointF(center.X - cross, center.Y),
					new PointF(center.X + cross, center.Y)
				},
				Color = Color.Magenta,
				Width = 2.2f,
				Arrow = false
			});
			canvas.OverlayLines.Add(new ColoredPolyline
			{
				Points = new List<PointF>
				{
					new PointF(center.X, center.Y - cross),
					new PointF(center.X, center.Y + cross)
				},
				Color = Color.Magenta,
				Width = 2.2f,
				Arrow = false
			});
			canvas.OverlayPoints.Add(new SolidPoint
			{
				Center = center,
				Radius = 4.5f,
				Color = Color.Magenta,
				Label = $"模板中心 score={r.TemplateMatchScore:0.000}",
				LabelPriority = 80
			});
		}

		private static bool IsContourSeparator(PointF p)
		{
			return float.IsNaN(p.X) || float.IsNaN(p.Y);
		}

		private void SetReady(string status, string msg)
		{
			InspectionLanguage language = _job?.Language ?? InspectionLanguage.Chinese;
			lblStatus.Text = status;
			lblStatus.BackColor = Color.LightGray;
			lblSummary.Text = LocalizedText.Message(msg, language);
			lblSummary.ForeColor = Color.FromArgb(90, 90, 90);
			lblResultTip.Text = LocalizedText.Message("运行后会在左侧图像显示线、点和偏差标注", language);
			FixMultiLineLabel(lblSummary, 10);
			FixMultiLineLabel(lblResultTip, 10);
			canvas.ClearOverlays();
			canvas.ClearHud();
			canvas.ClearRuntimeRois();
			canvas.ShowRuntimeRois = false;
			canvas.ShowRois = true;
			canvas.Invalidate();
		}

		private void SetError(string status, string msg)
		{
			InspectionLanguage language = _job?.Language ?? InspectionLanguage.Chinese;
			lblStatus.Text = status;
			lblStatus.BackColor = Color.Goldenrod;
			lblSummary.Text = LocalizedText.Message(msg, language);
			lblSummary.ForeColor = Color.FromArgb(180, 120, 0);
			lblResultTip.Text = LocalizedText.Message("错误信息已显示在左侧图像提示中", language);
			FixMultiLineLabel(lblSummary, 10);
			FixMultiLineLabel(lblResultTip, 10);
			canvas.ClearOverlays();
			canvas.ClearRuntimeRois();
			canvas.ShowRuntimeRois = false;
			canvas.HudTextColor = Color.Gold;
			canvas.HudText = LocalizedText.Message("错误:\n" + msg, language);
			canvas.Invalidate();
		}

		private static PointF ProjectPointToLine(PointF p, PointF a, PointF b)
		{
			float num = b.X - a.X;
			float num2 = b.Y - a.Y;
			float num3 = num * num + num2 * num2;
			if (num3 < 1E-12f)
			{
				return a;
			}
			float num4 = ((p.X - a.X) * num + (p.Y - a.Y) * num2) / num3;
			return new PointF(a.X + num4 * num, a.Y + num4 * num2);
		}

		private static bool TryGetLocalFitSegment(EdgeLineFit line, RotRectF roi, out PointF p1, out PointF p2)
		{
			p1 = (p2 = PointF.Empty);
			if (line == null || !line.Success)
			{
				return false;
			}
			double dx = line.P2.X - line.P1.X;
			double dy = line.P2.Y - line.P1.Y;
			double length = Math.Sqrt(dx * dx + dy * dy);
			if (length < 1E-09)
			{
				return false;
			}
			dx /= length;
			dy /= length;
			List<PointF> measurePoints = line.MeasurePoints?.Where((PointF x) => !float.IsNaN(x.X) && !float.IsNaN(x.Y)).ToList() ?? new List<PointF>();
			if (measurePoints.Count >= 2)
			{
				double min = double.PositiveInfinity;
				double max = double.NegativeInfinity;
				foreach (PointF point in measurePoints)
				{
					double projection = (point.X - line.P1.X) * dx + (point.Y - line.P1.Y) * dy;
					min = Math.Min(min, projection);
					max = Math.Max(max, projection);
				}
				double padding = Math.Max(4.0, Math.Min(12.0, (max - min) * 0.08));
				min -= padding;
				max += padding;
				p1 = new PointF((float)(line.P1.X + min * dx), (float)(line.P1.Y + min * dy));
				p2 = new PointF((float)(line.P1.X + max * dx), (float)(line.P1.Y + max * dy));
				return true;
			}
			PointF center = ProjectPointToLine(roi.Center, line.P1, line.P2);
			double halfLength = Math.Max(10.0, Math.Max(roi.HalfLen1, roi.HalfLen2));
			p1 = new PointF((float)(center.X - dx * halfLength), (float)(center.Y - dy * halfLength));
			p2 = new PointF((float)(center.X + dx * halfLength), (float)(center.Y + dy * halfLength));
			return true;
		}

		private void LayoutRightPanelOnePage()
		{
			if (rightPanel != null && !rightPanel.IsDisposed)
			{
				int num = 8;
				int num2 = 8;
				int num3 = Math.Max(200, rightPanel.ClientSize.Width - num * 2);
				int num4 = 176;
				int num5 = 110;
				grpOps.SetBounds(num, num, num3, num4);
				int num6 = grpOps.Bottom + num2;
				int num7 = rightPanel.ClientSize.Height - num6 - num2 - num5 - num;
				if (num7 < 560)
				{
					num7 = 560;
				}
				grpParams.SetBounds(num, num6, num3, num7);
				cardResult.SetBounds(num, grpParams.Bottom + num2, num3, num5);
				LayoutResultCard();
			}
		}

		private void LayoutResultCard()
		{
			if (cardResult != null && !cardResult.IsDisposed)
			{
				int num = cardResult.ClientSize.Width;
				int num2 = cardResult.ClientSize.Height;
				lblResultTitle.SetBounds(12, 8, 100, 20);
				int num3 = 122;
				int num4 = Math.Max(66, num2 - 40);
				lblStatus.SetBounds(12, 30, num3, num4);
				int num5 = 146;
				int num6 = Math.Max(80, num - num5 - 12);
				lblSummary.SetBounds(num5, 30, num6, 40);
				lblResultTip.SetBounds(num5, num2 - 30, num6, 20);
				FixMultiLineLabel(lblSummary, 12);
				FixMultiLineLabel(lblResultTip, 12);
			}
		}

		private void BtnConfirmInternal()
		{
			try
			{
				ReturnedJob = BuildJobForReturn();
				_job = ReturnedJob.DeepClone();
				_savedJob = ReturnedJob.DeepClone();
				_hasUnsavedChanges = false;
				base.DialogResult = DialogResult.OK;
				Close();
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "确认配置失败", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && components != null)
			{
				components.Dispose();
			}
			base.Dispose(disposing);
		}

		private void InitializeComponent()
		{
			this.components = new System.ComponentModel.Container();
			this.splitMain = new System.Windows.Forms.SplitContainer();
			this.canvas = new EdgeAlignInspect.HalconCanvas();
			this.rightPanel = new System.Windows.Forms.Panel();
			this.grpOps = new System.Windows.Forms.GroupBox();
			this.btnLoad = new System.Windows.Forms.Button();
			this.btnAddBaseRoi = new System.Windows.Forms.Button();
			this.btnAddCircleBaseRoi = new System.Windows.Forms.Button();
			this.btnAddCirclePointRoi = new System.Windows.Forms.Button();
			this.btnAddDetectRoi = new System.Windows.Forms.Button();
			this.btnDeleteRoi = new System.Windows.Forms.Button();
			this.btnTemplateSettings = new System.Windows.Forms.Button();
			this.btnSaveTeach = new System.Windows.Forms.Button();
			this.btnRun = new System.Windows.Forms.Button();
			this.btnConfirm = new System.Windows.Forms.Button();
			this.cboLanguage = new System.Windows.Forms.ComboBox();
			this.lblRoiHint = new System.Windows.Forms.Label();
			this.grpParams = new System.Windows.Forms.GroupBox();
			this.chkDetectEnabled = new System.Windows.Forms.CheckBox();
			this.numNominal = new System.Windows.Forms.NumericUpDown();
			this.cboAngleRef = new System.Windows.Forms.ComboBox();
			this.cboDetectBindBase = new System.Windows.Forms.ComboBox();
			this.cboDetectMode = new System.Windows.Forms.ComboBox();
			this.numConsecutiveAbnormalPoints = new System.Windows.Forms.NumericUpDown();
			this.chkUseReferenceLine = new System.Windows.Forms.CheckBox();
			this.cboBasePol = new System.Windows.Forms.ComboBox();
			this.cboDetPol = new System.Windows.Forms.ComboBox();
			this.numBaseMeasures = new System.Windows.Forms.NumericUpDown();
			this.numBaseSigma = new System.Windows.Forms.NumericUpDown();
			this.numBaseTh = new System.Windows.Forms.NumericUpDown();
			this.numBaseOutward = new System.Windows.Forms.NumericUpDown();
			this.numDetMeasures = new System.Windows.Forms.NumericUpDown();
			this.numDetSigma = new System.Windows.Forms.NumericUpDown();
			this.numDetTh = new System.Windows.Forms.NumericUpDown();
			this.numDetFitRatio = new System.Windows.Forms.NumericUpDown();
			this.chkEnableMatch = new System.Windows.Forms.CheckBox();
			this.chkEnableSearchRoi = new System.Windows.Forms.CheckBox();
			this.numMatchMinScore = new System.Windows.Forms.NumericUpDown();
			this.numMatchAngleStart = new System.Windows.Forms.NumericUpDown();
			this.numMatchAngleExtent = new System.Windows.Forms.NumericUpDown();
			this.cardResult = new System.Windows.Forms.Panel();
			this.lblResultTitle = new System.Windows.Forms.Label();
			this.lblStatus = new System.Windows.Forms.Label();
			this.lblSummary = new System.Windows.Forms.Label();
			this.lblResultTip = new System.Windows.Forms.Label();
			((System.ComponentModel.ISupportInitialize)this.splitMain).BeginInit();
			this.splitMain.Panel1.SuspendLayout();
			this.splitMain.Panel2.SuspendLayout();
			this.splitMain.SuspendLayout();
			this.rightPanel.SuspendLayout();
			this.grpOps.SuspendLayout();
			this.grpParams.SuspendLayout();
			this.cardResult.SuspendLayout();
			((System.ComponentModel.ISupportInitialize)this.numNominal).BeginInit();
			((System.ComponentModel.ISupportInitialize)this.numConsecutiveAbnormalPoints).BeginInit();
			((System.ComponentModel.ISupportInitialize)this.numBaseMeasures).BeginInit();
			((System.ComponentModel.ISupportInitialize)this.numBaseSigma).BeginInit();
			((System.ComponentModel.ISupportInitialize)this.numBaseTh).BeginInit();
			((System.ComponentModel.ISupportInitialize)this.numBaseOutward).BeginInit();
			((System.ComponentModel.ISupportInitialize)this.numDetMeasures).BeginInit();
			((System.ComponentModel.ISupportInitialize)this.numDetSigma).BeginInit();
			((System.ComponentModel.ISupportInitialize)this.numDetTh).BeginInit();
			((System.ComponentModel.ISupportInitialize)this.numDetFitRatio).BeginInit();
			((System.ComponentModel.ISupportInitialize)this.numMatchMinScore).BeginInit();
			((System.ComponentModel.ISupportInitialize)this.numMatchAngleStart).BeginInit();
			((System.ComponentModel.ISupportInitialize)this.numMatchAngleExtent).BeginInit();
			base.SuspendLayout();
			base.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
			this.Font = new System.Drawing.Font("微软雅黑", 9f);
			base.ClientSize = new System.Drawing.Size(1320, 780);
			this.MinimumSize = new System.Drawing.Size(1160, 700);
			this.Text = "智茂检测";
			base.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
			base.MaximizeBox = false;
			this.splitMain.Dock = System.Windows.Forms.DockStyle.Fill;
			this.splitMain.FixedPanel = System.Windows.Forms.FixedPanel.Panel2;
			this.splitMain.SplitterWidth = 6;
			this.canvas.Dock = System.Windows.Forms.DockStyle.Fill;
			this.canvas.BackColor = System.Drawing.Color.Black;
			this.splitMain.Panel1.Controls.Add(this.canvas);
			this.rightPanel.Dock = System.Windows.Forms.DockStyle.Fill;
			this.rightPanel.BackColor = System.Drawing.Color.FromArgb(247, 248, 250);
			this.rightPanel.Padding = new System.Windows.Forms.Padding(10);
			this.rightPanel.AutoScroll = true;
			this.rightPanel.Controls.Add(this.grpOps);
			this.rightPanel.Controls.Add(this.grpParams);
			this.rightPanel.Controls.Add(this.cardResult);
			this.splitMain.Panel2.Controls.Add(this.rightPanel);
			this.grpOps.Text = "操作";
			this.grpOps.Font = new System.Drawing.Font("微软雅黑", 9f, System.Drawing.FontStyle.Bold);
			this.grpOps.SetBounds(8, 8, 476, 176);
			this.grpOps.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
			EdgeAlignInspect.Form1.SetupButton(this.btnTemplateSettings, "模板设置", 244, 60, 124, 30, false);
			EdgeAlignInspect.Form1.SetupButton(this.btnLoad, "加载图片", 14, 60, 96, 30, false);
			EdgeAlignInspect.Form1.SetupButton(this.btnDeleteRoi, "删除选中ROI", 118, 60, 118, 30, false);
			EdgeAlignInspect.Form1.SetupButton(this.btnRun, "运行", 376, 24, 96, 30, true);
			EdgeAlignInspect.Form1.SetupButton(this.btnAddBaseRoi, "添加线基准", 14, 96, 104, 30, false);
			EdgeAlignInspect.Form1.SetupButton(this.btnAddCircleBaseRoi, "添加圆基准", 126, 96, 104, 30, false);
			EdgeAlignInspect.Form1.SetupButton(this.btnAddCirclePointRoi, "添加圆点基准", 238, 96, 122, 30, false);
			EdgeAlignInspect.Form1.SetupButton(this.btnAddDetectRoi, "添加检测ROI", 368, 96, 104, 30, false);
			EdgeAlignInspect.Form1.SetupButton(this.btnSaveTeach, "保存(示教)", 14, 132, 222, 30, false);
			EdgeAlignInspect.Form1.SetupButton(this.btnConfirm, "确认配置", 250, 132, 222, 30, false);
			EdgeAlignInspect.Form1.AddLabel(this.grpOps, "语言", 14, 24, 48, 28);
			EdgeAlignInspect.Form1.SetupLanguageCombo(this.cboLanguage, 70, 24, 130, 28);
			this.lblRoiHint.Text = "直接点击左侧图像中的 ROI 进行选中、拖动、旋转和缩放；毛刺公差由上位机传入。";
			this.lblRoiHint.Font = new System.Drawing.Font("微软雅黑", 8.5f, System.Drawing.FontStyle.Regular);
			this.lblRoiHint.ForeColor = System.Drawing.Color.FromArgb(90, 90, 90);
			this.lblRoiHint.AutoSize = false;
			this.lblRoiHint.SetBounds(14, 168, 458, 22);
			this.lblRoiHint.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
			this.grpOps.Controls.Add(this.btnLoad);
			this.grpOps.Controls.Add(this.btnDeleteRoi);
			this.grpOps.Controls.Add(this.btnTemplateSettings);
			this.grpOps.Controls.Add(this.btnRun);
			this.grpOps.Controls.Add(this.btnAddBaseRoi);
			this.grpOps.Controls.Add(this.btnAddCircleBaseRoi);
			this.grpOps.Controls.Add(this.btnAddCirclePointRoi);
			this.grpOps.Controls.Add(this.btnAddDetectRoi);
			this.grpOps.Controls.Add(this.btnSaveTeach);
			this.grpOps.Controls.Add(this.btnConfirm);
			this.grpOps.Controls.Add(this.cboLanguage);
			this.grpOps.Controls.Add(this.lblRoiHint);
			this.grpParams.Text = "参数";
			this.grpParams.Font = new System.Drawing.Font("微软雅黑", 9f, System.Drawing.FontStyle.Bold);
			this.grpParams.SetBounds(8, 192, 476, 470);
			this.grpParams.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
			int num = 28;
			int num2 = 8;
			int num3 = 20;
			EdgeAlignInspect.Form1.AddSubTitle(this.grpParams, "当前选中检测ROI参数", 14, num3, 446);
			num3 += 26;
			this.chkDetectEnabled.Text = "启用当前检测ROI";
			this.chkDetectEnabled.AutoSize = false;
			this.chkDetectEnabled.SetBounds(100, num3, 160, num);
			this.grpParams.Controls.Add(this.chkDetectEnabled);
			num3 += num + num2;
			EdgeAlignInspect.Form1.AddLabel(this.grpParams, "关联基准ROI", 14, num3, 78, num);
			EdgeAlignInspect.Form1.SetupCombo(this.cboDetectBindBase, 100, num3, 150, num, 180);
			this.grpParams.Controls.Add(this.cboDetectBindBase);
			EdgeAlignInspect.Form1.AddLabel(this.grpParams, "角度参考", 260, num3, 58, num);
			EdgeAlignInspect.Form1.SetupAngleRefCombo(this.cboAngleRef, 322, num3, 138, num);
			this.grpParams.Controls.Add(this.cboAngleRef);
			num3 += num + num2;
			EdgeAlignInspect.Form1.AddLabel(this.grpParams, "标准距离(mm)", 14, num3, 78, num);
			EdgeAlignInspect.Form1.SetupNum(this.numNominal, -10000m, 10000m, 2, 0m, 100, num3, 150, num);
			this.grpParams.Controls.Add(this.numNominal);
			num3 += num + 12;
			int num4 = 14;
			int num5 = 216;
			int num6 = 244;
			int num7 = 216;
			int num8 = num3;
			EdgeAlignInspect.Form1.AddSubTitle(this.grpParams, "全局判定参数", num4, num8, num5);
			int num9 = num8 + 26;
			this.chkUseReferenceLine.Text = "使用基准线判定";
			this.chkUseReferenceLine.AutoSize = false;
			this.chkUseReferenceLine.SetBounds(num4 + 82, num9, 128, num);
			this.grpParams.Controls.Add(this.chkUseReferenceLine);
			num9 += num + num2;
			EdgeAlignInspect.Form1.AddLabel(this.grpParams, "检测模式", num4, num9, 72, num);
			EdgeAlignInspect.Form1.SetupDetectModeCombo(this.cboDetectMode, num4 + 82, num9, 128, num);
			this.grpParams.Controls.Add(this.cboDetectMode);
			num9 += num + num2;
			EdgeAlignInspect.Form1.AddLabel(this.grpParams, "连续异常点", num4, num9, 72, num);
			EdgeAlignInspect.Form1.SetupNum(this.numConsecutiveAbnormalPoints, 1m, 500m, 0, 2m, num4 + 82, num9, 128, num);
			this.grpParams.Controls.Add(this.numConsecutiveAbnormalPoints);
			num9 += num + 12;
			EdgeAlignInspect.Form1.AddSubTitle(this.grpParams, "模板匹配", num4, num9, num5);
			num9 += 26;
			this.chkEnableMatch.Text = "启用模板匹配";
			this.chkEnableMatch.Checked = true;
			this.chkEnableMatch.AutoSize = false;
			this.chkEnableMatch.SetBounds(num4 + 82, num9, 128, num);
			this.grpParams.Controls.Add(this.chkEnableMatch);
			num9 += num + num2;
			this.chkEnableSearchRoi.Text = "启用模板搜索区域";
			this.chkEnableSearchRoi.Checked = true;
			this.chkEnableSearchRoi.AutoSize = false;
			this.chkEnableSearchRoi.SetBounds(num4, num9, num5, num);
			this.grpParams.Controls.Add(this.chkEnableSearchRoi);
			num9 += num + num2;
			EdgeAlignInspect.Form1.AddLabel(this.grpParams, "最小分数", num4, num9, 72, num);
			EdgeAlignInspect.Form1.SetupNum(this.numMatchMinScore, 0.00m, 1.00m, 2, 0.50m, num4 + 82, num9, 128, num);
			this.grpParams.Controls.Add(this.numMatchMinScore);
			num9 += num + num2;
			EdgeAlignInspect.Form1.AddLabel(this.grpParams, "角度起始", num4, num9, 72, num);
			EdgeAlignInspect.Form1.SetupNum(this.numMatchAngleStart, -3.14m, 3.14m, 3, -0.300m, num4 + 82, num9, 128, num);
			this.grpParams.Controls.Add(this.numMatchAngleStart);
			num9 += num + num2;
			EdgeAlignInspect.Form1.AddLabel(this.grpParams, "角度范围", num4, num9, 72, num);
			EdgeAlignInspect.Form1.SetupNum(this.numMatchAngleExtent, 0.00m, 6.28m, 3, 0.600m, num4 + 82, num9, 128, num);
			this.grpParams.Controls.Add(this.numMatchAngleExtent);
			EdgeAlignInspect.Form1.AddSubTitle(this.grpParams, "找边极性", num6, num8, num7);
			int num10 = num8 + 26;
			EdgeAlignInspect.Form1.AddLabel(this.grpParams, "基准极性", num6, num10, 72, num);
			EdgeAlignInspect.Form1.SetupPolCombo(this.cboBasePol, num6 + 82, num10, 128, num);
			this.grpParams.Controls.Add(this.cboBasePol);
			num10 += num + num2;
			EdgeAlignInspect.Form1.AddLabel(this.grpParams, "检测极性", num6, num10, 72, num);
			EdgeAlignInspect.Form1.SetupPolCombo(this.cboDetPol, num6 + 82, num10, 128, num);
			this.grpParams.Controls.Add(this.cboDetPol);
			num10 += num + 12;
			EdgeAlignInspect.Form1.AddSubTitle(this.grpParams, "卡尺参数", num6, num10, num7);
			num10 += 26;
			EdgeAlignInspect.Form1.AddSmallHeader(this.grpParams, "基准", num6 + 64, num10, 56);
			EdgeAlignInspect.Form1.AddSmallHeader(this.grpParams, "检测", num6 + 140, num10, 56);
			num10 += 20;
			EdgeAlignInspect.Form1.AddMiniLabel(this.grpParams, "点数", num6, num10, 36);
			EdgeAlignInspect.Form1.SetupNum(this.numBaseMeasures, 1m, 500m, 0, 30m, num6 + 44, num10, 74, num);
			this.grpParams.Controls.Add(this.numBaseMeasures);
			EdgeAlignInspect.Form1.SetupNum(this.numDetMeasures, 1m, 500m, 0, 40m, num6 + 128, num10, 74, num);
			this.grpParams.Controls.Add(this.numDetMeasures);
			num10 += num + num2;
			EdgeAlignInspect.Form1.AddMiniLabel(this.grpParams, "Sigma", num6, num10, 36);
			EdgeAlignInspect.Form1.SetupNum(this.numBaseSigma, 0.1m, 50m, 1, 1.0m, num6 + 44, num10, 74, num);
			this.grpParams.Controls.Add(this.numBaseSigma);
			EdgeAlignInspect.Form1.SetupNum(this.numDetSigma, 0.1m, 50m, 1, 1.0m, num6 + 128, num10, 74, num);
			this.grpParams.Controls.Add(this.numDetSigma);
			num10 += num + num2;
			EdgeAlignInspect.Form1.AddMiniLabel(this.grpParams, "阈值", num6, num10, 36);
			EdgeAlignInspect.Form1.SetupNum(this.numBaseTh, 1m, 255m, 0, 20m, num6 + 44, num10, 74, num);
			this.grpParams.Controls.Add(this.numBaseTh);
			EdgeAlignInspect.Form1.SetupNum(this.numDetTh, 1m, 255m, 0, 15m, num6 + 128, num10, 74, num);
			this.grpParams.Controls.Add(this.numDetTh);
			num10 += num + num2;
			EdgeAlignInspect.Form1.AddMiniLabel(this.grpParams, "圆外扩", num6, num10, 42);
			EdgeAlignInspect.Form1.SetupNum(this.numBaseOutward, 0m, 2000m, 1, 6.0m, num6 + 44, num10, 52, num);
			this.grpParams.Controls.Add(this.numBaseOutward);
			EdgeAlignInspect.Form1.AddMiniLabel(this.grpParams, "正常点占比", num6 + 100, num10, 72);
			EdgeAlignInspect.Form1.SetupNum(this.numDetFitRatio, 10m, 100m, 0, 60m, num6 + 172, num10, 44, num);
			this.grpParams.Controls.Add(this.numDetFitRatio);
			this.cardResult.SetBounds(8, 658, 476, 110);
			this.cardResult.BackColor = System.Drawing.Color.White;
			this.cardResult.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
			this.cardResult.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
			this.lblResultTitle.Text = "检测结果";
			this.lblResultTitle.Font = new System.Drawing.Font("微软雅黑", 9f, System.Drawing.FontStyle.Bold);
			this.lblResultTitle.ForeColor = System.Drawing.Color.FromArgb(55, 55, 55);
			this.lblResultTitle.SetBounds(12, 8, 100, 20);
			this.lblStatus.Text = "READY";
			this.lblStatus.Font = new System.Drawing.Font("Arial", 20f, System.Drawing.FontStyle.Bold);
			this.lblStatus.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
			this.lblStatus.BackColor = System.Drawing.Color.LightGray;
			this.lblStatus.ForeColor = System.Drawing.Color.White;
			this.lblStatus.SetBounds(12, 30, 122, 66);
			this.lblSummary.Font = new System.Drawing.Font("微软雅黑", 9f);
			this.lblSummary.ForeColor = System.Drawing.Color.FromArgb(90, 90, 90);
			this.lblSummary.AutoSize = false;
			this.lblSummary.SetBounds(146, 30, 316, 40);
			this.lblResultTip.Text = "运行后会在左侧图像显示线、点和偏差标注";
			this.lblResultTip.Font = new System.Drawing.Font("微软雅黑", 8.5f);
			this.lblResultTip.ForeColor = System.Drawing.Color.FromArgb(120, 120, 120);
			this.lblResultTip.AutoSize = false;
			this.lblResultTip.SetBounds(146, 74, 316, 22);
			this.cardResult.Controls.Add(this.lblResultTitle);
			this.cardResult.Controls.Add(this.lblStatus);
			this.cardResult.Controls.Add(this.lblSummary);
			this.cardResult.Controls.Add(this.lblResultTip);
			base.Controls.Add(this.splitMain);
			this.splitMain.Panel1.ResumeLayout(false);
			this.splitMain.Panel2.ResumeLayout(false);
			((System.ComponentModel.ISupportInitialize)this.splitMain).EndInit();
			this.splitMain.ResumeLayout(false);
			this.rightPanel.ResumeLayout(false);
			this.grpOps.ResumeLayout(false);
			this.grpParams.ResumeLayout(false);
			this.cardResult.ResumeLayout(false);
			((System.ComponentModel.ISupportInitialize)this.numNominal).EndInit();
			((System.ComponentModel.ISupportInitialize)this.numConsecutiveAbnormalPoints).EndInit();
			((System.ComponentModel.ISupportInitialize)this.numBaseMeasures).EndInit();
			((System.ComponentModel.ISupportInitialize)this.numBaseSigma).EndInit();
			((System.ComponentModel.ISupportInitialize)this.numBaseTh).EndInit();
			((System.ComponentModel.ISupportInitialize)this.numBaseOutward).EndInit();
			((System.ComponentModel.ISupportInitialize)this.numDetMeasures).EndInit();
			((System.ComponentModel.ISupportInitialize)this.numDetSigma).EndInit();
			((System.ComponentModel.ISupportInitialize)this.numDetTh).EndInit();
			((System.ComponentModel.ISupportInitialize)this.numDetFitRatio).EndInit();
			((System.ComponentModel.ISupportInitialize)this.numMatchMinScore).EndInit();
			((System.ComponentModel.ISupportInitialize)this.numMatchAngleStart).EndInit();
			((System.ComponentModel.ISupportInitialize)this.numMatchAngleExtent).EndInit();
			base.ResumeLayout(false);
		}

		private static void SetupButton(Button b, string text, int x, int y, int w, int h, bool primary)
		{
			b.Text = text;
			b.SetBounds(x, y, w, h);
			b.FlatStyle = FlatStyle.Flat;
			b.FlatAppearance.BorderSize = 1;
			b.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 210);
			b.BackColor = (primary ? Color.FromArgb(60, 110, 245) : Color.White);
			b.ForeColor = (primary ? Color.White : Color.FromArgb(40, 40, 40));
			b.Cursor = Cursors.Hand;
			b.UseVisualStyleBackColor = false;
		}

		private static void AddLabel(Control parent, string text, int x, int y, int w, int h)
		{
			Label label = new Label
			{
				Text = text,
				TextAlign = ContentAlignment.MiddleRight,
				Font = new Font("微软雅黑", 9f, FontStyle.Regular),
				AutoSize = false
			};
			label.SetBounds(x, y + 1, w, h);
			parent.Controls.Add(label);
		}

		private static void AddMiniLabel(Control parent, string text, int x, int y, int w)
		{
			Label label = new Label
			{
				Text = text,
				TextAlign = ContentAlignment.MiddleLeft,
				Font = new Font("微软雅黑", 8.5f, FontStyle.Regular),
				AutoSize = false
			};
			label.SetBounds(x, y + 2, w, 20);
			parent.Controls.Add(label);
		}

		private static void AddSubTitle(Control parent, string text, int x, int y, int width)
		{
			Label label = new Label
			{
				Text = text,
				Font = new Font("微软雅黑", 9f, FontStyle.Bold),
				ForeColor = Color.FromArgb(50, 50, 50),
				AutoSize = false
			};
			label.SetBounds(x, y, width, 18);
			parent.Controls.Add(label);
			Label label2 = new Label
			{
				BackColor = Color.FromArgb(225, 225, 225),
				AutoSize = false
			};
			label2.SetBounds(x, y + 18, width, 1);
			parent.Controls.Add(label2);
		}

		private static void AddSmallHeader(Control parent, string text, int x, int y, int w)
		{
			Label label = new Label
			{
				Text = text,
				Font = new Font("微软雅黑", 8.5f, FontStyle.Bold),
				ForeColor = Color.FromArgb(80, 80, 80),
				TextAlign = ContentAlignment.MiddleCenter,
				AutoSize = false
			};
			label.SetBounds(x, y, w, 16);
			parent.Controls.Add(label);
		}

		private static void SetupNum(NumericUpDown n, decimal min, decimal max, int decimals, decimal val, int x, int y, int w, int h)
		{
			n.Minimum = min;
			n.Maximum = max;
			n.DecimalPlaces = decimals;
			if (val < min)
			{
				val = min;
			}
			if (val > max)
			{
				val = max;
			}
			n.Value = val;
			n.SetBounds(x, y, w, h);
			n.TextAlign = HorizontalAlignment.Left;
		}

		private static void SetupCombo(ComboBox c, int x, int y, int w, int h, int dropDownWidth)
		{
			c.DropDownStyle = ComboBoxStyle.DropDownList;
			c.SetBounds(x, y, w, h);
			c.DropDownWidth = dropDownWidth;
		}

		private static void SetupLanguageCombo(ComboBox c, int x, int y, int w, int h)
		{
			c.DropDownStyle = ComboBoxStyle.DropDownList;
			c.Items.Clear();
			c.Items.AddRange(new object[2] { "中文", "English" });
			c.SelectedIndex = 0;
			c.SetBounds(x, y, w, h);
			c.DropDownWidth = 120;
		}

		private static void SetupPolCombo(ComboBox c, int x, int y, int w, int h)
		{
			c.DropDownStyle = ComboBoxStyle.DropDownList;
			c.Items.Clear();
			c.Items.AddRange(new object[3] { "白找黑", "黑找白", "任意" });
			c.SelectedIndex = 0;
			c.SetBounds(x, y, w, h);
			c.DropDownWidth = 140;
		}

		private static void SetupDetectModeCombo(ComboBox c, int x, int y, int w, int h)
		{
			c.DropDownStyle = ComboBoxStyle.DropDownList;
			c.Items.Clear();
			c.Items.AddRange(new object[3] { "两者都检测", "只检测毛刺", "只检测凹陷" });
			c.SelectedIndex = 0;
			c.SetBounds(x, y, w, h);
			c.DropDownWidth = 180;
		}

		private static void SetupAngleRefCombo(ComboBox c, int x, int y, int w, int h)
		{
			c.DropDownStyle = ComboBoxStyle.DropDownList;
			c.Items.Clear();
			c.Items.AddRange(new object[3] { "平行于关联基准线", "水平", "竖直" });
			c.SelectedIndex = 0;
			c.SetBounds(x, y, w, h);
			c.DropDownWidth = 220;
		}
	}
}
