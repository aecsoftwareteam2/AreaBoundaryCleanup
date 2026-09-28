using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Button;

namespace AreaBoundaryCleanup
{
    public class Ribbon : IExternalApplication
    {
        private const string _kRibbonNameAreaBoundaryCleanup = "Area Boundary Cleanup";

        public Result OnStartup(UIControlledApplication pControlledApplication)
        {
            try
            {
                pControlledApplication.CreateRibbonTab(_kRibbonNameAreaBoundaryCleanup);     
            }
            catch
            {
                // Tab already exists (e.g. multiple add-ins sharing it) — safe to ignore  
            }       
             
            RibbonPanel pRibbonPanel = pControlledApplication.CreateRibbonPanel(_kRibbonNameAreaBoundaryCleanup, "BoundaryLines Cleanup");
            string dllPathFile = Assembly.GetExecutingAssembly().Location;  

            string commandName = "Cleanup All";
            PushButtonData pPushButtonData = new PushButtonData("CleanupAll", commandName, dllPathFile, "AreaBoundaryCleanup.AreaBoundaryCleanupCommand");
            AddPushButtonDataToPanel(commandName, AreaBoundaryCleanup.Properties.Resource.Areaberakning, pPushButtonData, pRibbonPanel);

            pRibbonPanel.AddSeparator(); 

            commandName = "AreaBoundaryCleanup\nSelected Command";  
            pPushButtonData = new PushButtonData("AreaBoundaryCleanupSelectedCommand", commandName, dllPathFile, "AreaBoundaryCleanup.AreaBoundaryCleanupSelectedCommand");
            AddPushButtonDataToPanel(commandName, AreaBoundaryCleanup.Properties.Resource.Areaberakning, pPushButtonData, pRibbonPanel);

            /*pPushButton.LargeImage = GetImage(pBitmapImage.GetHbitmap());
            pPushButton.Image = pPushButton.LargeImage;*/

            /*PushButtonData cleanSelectedData = new PushButtonData(  
                "CleanSelected",
                "Cleanup\nSelected",
                assemblyPath,
                "AreaBoundaryCleanup.AreaBoundaryCleanupSelectedCommand")
            {
                ToolTip = "Pick specific Area Boundary Lines to clean up.",
                LargeImage = TryLoadImage("Resources/icon32.png")
            };*/

            //pRibbonPanel.AddItem(pPushButtonData);
            //pRibbonPanel.AddItem(cleanSelectedData);

            return Result.Succeeded; 
        }

        private void AddPushButtonDataToPanel(string commandName, Bitmap pBitmapImage, PushButtonData pPushButtonData, RibbonPanel pRibbonPanel)
        {
            Autodesk.Revit.UI.PushButton pPushButton = (Autodesk.Revit.UI.PushButton) pRibbonPanel.AddItem(pPushButtonData);
            pPushButton.LargeImage = GetImage(pBitmapImage.GetHbitmap());
            pPushButton.Image = pPushButton.LargeImage;
            pPushButton.ToolTip = commandName; 
            pPushButton.ToolTipImage = pPushButton.LargeImage;
        }

        public Result OnShutdown(UIControlledApplication pControlledApplication)
        {
            return Result.Succeeded;
        }

        private BitmapSource GetImage(IntPtr intPtr)
        {
            BitmapSource pBitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(intPtr, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            return pBitmapSource;
        }
    }
}
