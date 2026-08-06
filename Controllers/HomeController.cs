using Microsoft.AspNetCore.Mvc;
using PmoNav.Services;

namespace PmoNav.Controllers;

public class HomeController : Controller
{
    private readonly IDataService        _data;
    private readonly ICurrentUserService _user;

    public HomeController(IDataService data, ICurrentUserService user)
    {
        _data = data;
        _user = user;
    }

    public IActionResult Index()
    {
        ViewBag.Login      = _user.Login;
        ViewBag.Display    = _user.Display;
        ViewBag.LastLoaded = _data.LastLoaded;
        return View();
    }
}
