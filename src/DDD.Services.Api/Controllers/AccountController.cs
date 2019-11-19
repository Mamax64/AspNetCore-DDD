using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using DDD.Domain.Core.Bus;
using DDD.Domain.Core.Notifications;
using DDD.Infra.CrossCutting.Identity.Data;
using DDD.Infra.CrossCutting.Identity.Models;
using DDD.Infra.CrossCutting.Identity.Models.AccountViewModels;
using DDD.Infra.CrossCutting.Identity.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DDD.Services.Api.Controllers
{
    [Authorize]
    public class AccountController : ApiController
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly ILogger _logger;
        private readonly IJwtFactory _jwtFactory;
        private readonly ApplicationDbContext _dbContext;

        public AccountController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RoleManager<IdentityRole> roleManager,
            ApplicationDbContext dbContext,
            ILoggerFactory loggerFactory,
            IJwtFactory jwtFactory,
            INotificationHandler<DomainNotification> notifications,
            IMediatorHandler mediator) : base(notifications, mediator)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _dbContext = dbContext;
            _logger = loggerFactory.CreateLogger<AccountController>();
            _jwtFactory = jwtFactory;
        }

        [HttpPost]
        [AllowAnonymous]
        [Route("login")]
        public async Task<IActionResult> Login([FromBody] LoginViewModel model)
        {
            if (!ModelState.IsValid)
            {
                NotifyModelStateErrors();
                return Response(model);
            }

            // Sign In
            var signInResult = await _signInManager.PasswordSignInAsync(model.Email, model.Password, false, true);
            if (!signInResult.Succeeded)
            {
                NotifyError(signInResult.ToString(), "Login failure");
                return Response();
            }

            // Get User
            var appUser = await _userManager.FindByEmailAsync(model.Email);
            //var appUser = _userManager.Users.SingleOrDefault(r => r.Email == model.Email);

            _logger.LogInformation(1, "User logged in.");
            return Response(await GenerateToken(appUser));
        }

        [HttpPost]
        [AllowAnonymous]
        [Route("register")]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid)
            {
                NotifyModelStateErrors();
                return Response(model);
            }

            // Add User
            var user = new ApplicationUser { UserName = model.Email, Email = model.Email };
            var identityResult = await _userManager.CreateAsync(user, model.Password);
            if (!identityResult.Succeeded)
            {
                AddIdentityErrors(identityResult);
                return Response(model);
            }

            // Add UserRoles
            identityResult = await _userManager.AddToRoleAsync(user, "Admin");
            if (!identityResult.Succeeded)
            {
                AddIdentityErrors(identityResult);
                return Response(model);
            }

            // Add RoleClaims

            // Add UserClaims
            var claims = new List<Claim>
            {
                new Claim("Customers", "Write"),
                new Claim("Customers", "Remove"),
            };
            await _userManager.AddClaimsAsync(user, claims);

            // SignIn
            //await _signInManager.SignInAsync(user, false);

            _logger.LogInformation(3, "User created a new account with password.");
            return Response();
        }

        [HttpPost]
        [AllowAnonymous]
        [Route("refresh")]
        public async Task<IActionResult> Refresh(TokenViewModel model)
        {
            if (!ModelState.IsValid)
            {
                NotifyModelStateErrors();
                return Response(model);
            }

            // Get current RefreshToken
            var refreshTokenCurrent = _dbContext.RefreshTokens.SingleOrDefault(x => x.Token == model.RefreshToken);
            if (refreshTokenCurrent is null)
                return Response(model);
            if (refreshTokenCurrent.ExpiryDate < DateTime.UtcNow)
                return Response(model);

            // Get User
            var appUser = await _userManager.FindByIdAsync(refreshTokenCurrent.UserId);
            if (appUser is null)
                return Response(model);

            // Remove current RefreshToken
            _dbContext.Remove(refreshTokenCurrent);
            await _dbContext.SaveChangesAsync();

            return Response(await GenerateToken(appUser));
        }

        private async Task<TokenViewModel> GenerateToken(ApplicationUser appUser)
        {
            // Init ClaimsIdentity
            var claimsIdentity = new ClaimsIdentity();

            // Get UserClaims
            var userClaims = await _userManager.GetClaimsAsync(appUser);
            claimsIdentity.AddClaims(userClaims);

            // Get UserRoles
            var userRoles = await _userManager.GetRolesAsync(appUser);
            claimsIdentity.AddClaims(userRoles.Select(role => new Claim(ClaimsIdentity.DefaultRoleClaimType, role)));
            // ClaimsIdentity.DefaultRoleClaimType & ClaimTypes.Role is the same

            // Get RoleClaims
            foreach (var userRole in userRoles)
            {
                var role = await _roleManager.FindByNameAsync(userRole);
                var roleClaims = await _roleManager.GetClaimsAsync(role);
                claimsIdentity.AddClaims(roleClaims);
            }

            // Generate access token
            var accessToken = await _jwtFactory.GenerateJwtToken(appUser.Email, claimsIdentity);

            // Add refresh token
            var refreshToken = new RefreshToken
            {
                Token = Guid.NewGuid().ToString("N"),
                UserId = appUser.Id,
                CreationDate = DateTime.UtcNow,
                ExpiryDate = DateTime.UtcNow.AddMinutes(90),
            };
            await _dbContext.RefreshTokens.AddAsync(refreshToken);
            await _dbContext.SaveChangesAsync();

            return new TokenViewModel
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken.Token,
            };
        }
    }
}